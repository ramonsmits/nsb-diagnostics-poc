using System;
using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mongo2Go;
using MongoDB.Driver;
using MongoDB.Driver.Core.Extensions.DiagnosticSources;
using NServiceBus;
using NServiceBus.Configuration.AdvancedExtensibility;
using NServiceBus.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ChildWorkerService
{
    public class Program
    {
        public const string EndpointName = "NsbActivities.ChildWorkerService";

        public static void Main(string[] args)
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                ActivityStopped = activity =>
                {
                    foreach (var (key, value) in activity.Baggage)
                    {
                        activity.AddTag(key, value);
                    }
                }
            };
            ActivitySource.AddActivityListener(listener);








            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseNServiceBus(hostBuilderContext =>
                {
                    var endpointConfiguration = new EndpointConfiguration(EndpointName);

                    endpointConfiguration.UseSerialization<SystemJsonSerializer>();

                    var transport = endpointConfiguration.UseTransport<RabbitMQTransport>();
                    transport.ConnectionString("host=localhost");
                    transport.UseConventionalRoutingTopology();

                    endpointConfiguration.UsePersistence<LearningPersistence>();

                    endpointConfiguration.EnableInstallers();

                    endpointConfiguration.AuditProcessedMessagesTo("audit");

                    var recoverability = endpointConfiguration.Recoverability();
                    recoverability.Immediate(i => i.NumberOfRetries(1));
                    recoverability.Delayed(i => i.NumberOfRetries(0));

                    var settings = endpointConfiguration.GetSettings();

                    settings.Set(new NServiceBus.Extensions.Diagnostics.InstrumentationOptions
                    {
                        CaptureMessageBody = true
                    });

                    // configure endpoint here
                    return endpointConfiguration;
                })
                .ConfigureServices(services =>
                {
                    var runner = MongoDbRunner.Start(singleNodeReplSet: true, singleNodeReplSetWaitTimeout: 20);

                    services.AddSingleton(runner);
                    var urlBuilder = new MongoUrlBuilder(runner.ConnectionString)
                    {
                        DatabaseName = "dev"
                    };
                    var mongoUrl = urlBuilder.ToMongoUrl();
                    var mongoClientSettings = MongoClientSettings.FromUrl(mongoUrl);
                    mongoClientSettings.ClusterConfigurator = cb => cb.Subscribe(new DiagnosticsActivityEventSubscriber(new InstrumentationOptions { CaptureCommandText = true }));
                    var mongoClient = new MongoClient(mongoClientSettings);
                    services.AddSingleton(mongoUrl);
                    services.AddSingleton(mongoClient);
                    services.AddTransient(provider => provider.GetService<MongoClient>().GetDatabase(provider.GetService<MongoUrl>().DatabaseName));
                    services.AddHostedService<Mongo2GoService>();
                    services.AddOpenTelemetryTracing(builder => builder
                        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(EndpointName))
                        .AddMongoDBInstrumentation()
                        .AddNServiceBusInstrumentation()
                        //.AddZipkinExporter(o =>
                        //{
                        //    o.Endpoint = new Uri("http://localhost:9411/api/v2/spans");
                        //})
                        .AddJaegerExporter(c =>
                        {
                            c.AgentHost = "localhost";
                            c.AgentPort = 6831;
                        })
                        .AddAzureMonitorTraceExporter(c =>
                        {
                            c.ConnectionString = Environment.GetEnvironmentVariable("APPINSIGHTS_INSTRUMENTATIONKEY");
                        })
                        );
                })
        ;
    }
}
