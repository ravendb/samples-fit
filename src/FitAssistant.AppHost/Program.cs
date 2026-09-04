using CommunityToolkit.Aspire.Hosting.RavenDB;

var builder = DistributedApplication.CreateBuilder(args);

var openAiApiKey = builder
    .AddParameter("openai-api-key", secret: true)
    .WithDescription("OpenAI API key.");

var ravenLicense = builder
    .AddParameter("ravendb-license", secret: true)
    .WithDescription("Your Developer license formatted as JSON.");

var maxGlobalRequests = builder
    .AddParameter("max-global-requests-per-15-min", "100")
    .WithDescription("Maximum API requests globally per 15 minutes");

var maxSessionRequests = builder
    .AddParameter("max-session-requests-per-30-sec", "5")
    .WithDescription("Maximum API requests per session per 30 seconds");

var dailyGoalsCadence = builder.AddParameter("daily-goals-cadence-seconds", "86400")
    .WithDescription("Daily-goals GenAI task cadence in seconds. 86400 = 24h (prod). Set 600 (10 min) for a fast demo loop.");

var minio = builder.AddMinioContainer("minio",
    rootUser:     builder.AddParameter("minio-user", "fitadmin"),
    rootPassword: builder.AddParameter("minio-pwd",  "fitadmin123", secret: true),
    port: 9000);

var serverSettings = RavenDBServerSettings.Unsecured();
serverSettings.Port = 8081;
serverSettings.TcpPort = 38889;

var ravenServer = builder.AddRavenDB("ravendb", serverSettings)
    .WithImage("ravendb/ravendb", "7.2.6")
    .WithIconName("Database")
    .WithEnvironment("RAVEN_License_Eula_Accepted", "true")
    .WithEnvironment("RAVEN_License", ravenLicense)
    .WithEnvironment("RAVEN_PublicServerUrl_Tcp", "tcp://ravendb.dev.internal:38889")
    .WaitFor(minio);

var ravendb = ravenServer.AddDatabase("FitAssistant");

// RabbitMQ
var rabbit = builder.AddRabbitMQ("rabbit").WithManagementPlugin();

// Feed microservice
var fitFeed = builder.AddProject<Projects.FitAssistant_FitFeed>("fit-feed")
    .WithReference(rabbit)
    .WaitFor(rabbit);

var backend = builder.AddDockerfile("backend", "../..", "src/FitAssistant.Backend/Dockerfile")
    .WithReference(ravendb)
    .WithReference(rabbit)
    .WithReference(fitFeed)
    .WithReference(minio)
    .WaitFor(ravendb)
    .WaitFor(rabbit)
    .WaitFor(minio)
    .WithEnvironment("OPENAI_API_KEY", openAiApiKey)
    .WithEnvironment("FIT_ASSISTANT_MAX_GLOBAL_REQUESTS_PER_15_MINUTES", maxGlobalRequests)
    .WithEnvironment("FIT_ASSISTANT_MAX_SESSION_REQUESTS_PER_30_SECONDS", maxSessionRequests)
    .WithEnvironment("FitAssistant__DailyGoalsCadenceSeconds", dailyGoalsCadence)
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints()
    .WithHttpCommand(
        path: "/api/seed/all",
        displayName: "Seed data",
        endpointName: "http",
        commandOptions: new HttpCommandOptions
        {
            Description = "Seed the database with sample data",
            IconName = "databaseArrowUp",
            IsHighlighted = true
        });
    

builder.AddNpmApp("frontend", "../FitAssistant.Frontend", "start")
    .WithNpmPackageInstallation()
    .WithReference(fitFeed)
    .WaitFor(backend)
    .WaitFor(fitFeed)
    .WithEnvironment("BROWSER", "none")
    .WithEnvironment("PORT", "3000")
    .WithEnvironment("REACT_APP_BACKEND_URL", ReferenceExpression.Create($"{backend.GetEndpoint("http")}"))
    .WithEnvironment("REACT_APP_FITFEED_URL", ReferenceExpression.Create($"{fitFeed.GetEndpoint("http")}"))
    .WithHttpEndpoint(port: 3000, env: "PORT")
    .WithExternalHttpEndpoints();

builder.Build().Run();
