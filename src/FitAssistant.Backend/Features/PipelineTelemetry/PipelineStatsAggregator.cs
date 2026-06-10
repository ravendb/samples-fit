using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using FitAssistant.Backend.Configuration;
using FitAssistant.Backend.Features.Trends;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;

namespace FitAssistant.Backend.Features.PipelineTelemetry;

public class PipelineStatsAggregator(
    IDocumentStore store,
    TrendsQueryService trends,
    IOptions<MinioOptions> minio,
    PipelineActivityBuffer activity)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public record TimelineEvent(DateTime At, string Kind, string Summary);

    public record Snapshot(
        QueueEtlBlock QueueEtl,
        OlapEtlBlock OlapEtl,
        MinioBlock Minio,
        [property: JsonPropertyName("duckdb")] DuckDbBlock DuckDb,
        List<TimelineEvent> Recent,
        DateTime GeneratedAt);

    public record QueueEtlBlock(string Task, long Published, long Transforms, long Errors, long Consumed);
    public record OlapEtlBlock(string Task, long Writes, long Errors);
    public record MinioBlock(string Bucket, int ParquetFiles, long TotalBytes);
    public record DuckDbBlock(bool Ready, long RowCount, long QueryCount);

    public async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var etlTask          = FetchEtlStatsAsync(ct);
        var trendsMinioTask  = FetchBucketStatsAsync(Constants.Etl.TrendsBucketName,
            k => k.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase), ct);
        var fitFeedTask      = FetchFitFeedAsync(ct);

        await Task.WhenAll(etlTask, trendsMinioTask, fitFeedTask);

        var etl         = etlTask.Result;
        var trendsMinio = trendsMinioTask.Result;
        var fitFeed     = fitFeedTask.Result;
        var activityEtl = etl.GetValueOrDefault(Constants.Etl.ActivityFeedEtlName);
        var olapEtl     = etl.GetValueOrDefault(Constants.Etl.TrendsOlapEtlName);

        activity.NoticeOlapWrites(olapEtl.Successes);

        var unified = activity.Snapshot()
            .Select(e => new TimelineEvent(e.At, e.Kind, e.Summary))
            .Concat(fitFeed.Recent)
            .OrderByDescending(e => e.At)
            .Take(50)
            .ToList();

        return new Snapshot(
            QueueEtl: new(
                Task:       Constants.Etl.ActivityFeedEtlName,
                Published:  activityEtl.Successes,
                Transforms: activityEtl.Transforms,
                Errors:     activityEtl.Errors,
                Consumed:   fitFeed.Consumed),
            OlapEtl: new(
                Task:   Constants.Etl.TrendsOlapEtlName,
                Writes: olapEtl.Successes,
                Errors: olapEtl.Errors),
            Minio: new(
                Bucket:       Constants.Etl.TrendsBucketName,
                ParquetFiles: trendsMinio.FileCount,
                TotalBytes:   trendsMinio.TotalBytes),
            DuckDb: new(
                Ready:      trends.IsReady,
                RowCount:   trends.GetTotalRowCount(),
                QueryCount: trends.QueryCount),
            Recent:      unified,
            GeneratedAt: DateTime.UtcNow);
    }

    private async Task<Dictionary<string, EtlStat>> FetchEtlStatsAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{store.Urls[0].TrimEnd('/')}/databases/{store.Database}/etl/stats";
            var json = await Http.GetFromJsonAsync<EtlStatsResponse>(url, ct);
            return json?.Results?.ToDictionary(r => r.TaskName, r =>
            {
                var s = r.Stats.FirstOrDefault()?.Statistics;
                return new EtlStat(
                    Successes:  s?.LoadSuccesses ?? 0,
                    Transforms: s?.TransformationSuccesses ?? 0,
                    Errors:     (s?.LoadErrors ?? 0) + (s?.TransformationErrors ?? 0));
            }) ?? new();
        }
        catch { return new(); }
    }

    private readonly record struct EtlStat(long Successes, long Transforms, long Errors);

    private async Task<(int FileCount, long TotalBytes)> FetchBucketStatsAsync(
        string bucketName, Func<string, bool> keyFilter, CancellationToken ct)
    {
        try
        {
            using var s3 = new AmazonS3Client(minio.Value.AccessKey, minio.Value.SecretKey, new AmazonS3Config
            {
                ServiceURL           = minio.Value.Endpoint,
                ForcePathStyle       = true,
                AuthenticationRegion = "us-east-1",
            });

            int files = 0;
            long bytes = 0;
            string? token = null;
            do
            {
                var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName        = bucketName,
                    ContinuationToken = token,
                }, ct);

                foreach (var o in resp.S3Objects ?? new())
                {
                    if (!keyFilter(o.Key)) continue;
                    files++;
                    bytes += o.Size.GetValueOrDefault();
                }
                token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
            } while (token != null);

            return (files, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task<(long Consumed, IReadOnlyList<TimelineEvent> Recent)> FetchFitFeedAsync(CancellationToken ct)
    {
        try
        {
            var url = Environment.GetEnvironmentVariable("services__fit-feed__http__0"); 
            var resp = await Http.GetFromJsonAsync<FitFeedStatsResponse>($"{url.TrimEnd('/')}/api/stats", ct);
            var recent = resp?.Recent?
                .Select(r => new TimelineEvent(r.At, r.Kind, r.Summary))
                .ToList() ?? new();
            return (resp?.Consumed ?? 0, recent);
        }
        catch { return (0, Array.Empty<TimelineEvent>()); }
    }

    private sealed class EtlStatsResponse
    {
        public List<EtlTaskStat>? Results { get; set; }
    }

    private sealed class EtlTaskStat
    {
        public string TaskName { get; set; } = "";
        public List<EtlTransformStats> Stats { get; set; } = new();
    }

    private sealed class EtlTransformStats
    {
        public EtlInnerStats Statistics { get; set; } = new();
    }

    private sealed class EtlInnerStats
    {
        public long LoadSuccesses           { get; set; }
        public long LoadErrors              { get; set; }
        public long TransformationSuccesses { get; set; }
        public long TransformationErrors    { get; set; }
    }

    private sealed class FitFeedStatsResponse
    {
        public long Consumed { get; set; }
        public List<FitFeedRecentEvent>? Recent { get; set; }
    }

    private sealed class FitFeedRecentEvent
    {
        public DateTime At      { get; set; }
        public string   Kind    { get; set; } = "";
        public string   Summary { get; set; } = "";
    }
}
