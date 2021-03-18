using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NUnit.VisualStudio.TestAdapter.Internal
{
    public class SeriLogger
    {
        public static ILogger Logger { get; set; }

        public static void Initialize() => Initialize(new());

        public static void Initialize(Configuration logConfiguration)
        {
            var flatLogPath = Path.Combine(Directory.GetCurrentDirectory(), "husky-test-runner.log");

            var loggerConfiguration = new LoggerConfiguration()
                                     .MinimumLevel.Verbose()
                                     .Destructure.AsScalar<DirectoryInfo>()
                                     .WriteTo.Seq(logConfiguration.SeqHttpUrl)
                                     .WriteTo.Async(a => a.File(path: flatLogPath, restrictedToMinimumLevel: LogEventLevel.Verbose));

            Logger = loggerConfiguration.CreateLogger();
            Logger.Information("Am I in container? {inContainer}", Docker.IsInDockerContainer());
            Logger.Information("Test Logger Initialized From Inside Test SDK");
            Logger.Debug("Logging to FileSystem at {loggerFilePath}", flatLogPath);
            Logger.Debug("Logging to Seq at {seqPath}", logConfiguration.SeqHttpUrl);
        }

        public static void Dispose() => (Logger as Logger)?.Dispose();

        public class Configuration
        {
            public readonly string SeqHttpUrl = Docker.IsInDockerContainer()
                ? $"http://{Docker.SeqContainerName}:{Docker.SeqPort}"
                : $"http://localhost:{Docker.SeqPort}";
        }
    }
}