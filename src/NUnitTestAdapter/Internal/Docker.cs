using System;
using System.IO;
using CliWrap;
using CliWrap.Buffered;
using NUnit.Engine;
using Serilog;

namespace NUnit.VisualStudio.TestAdapter.Internal
{
    public static class Docker
    {
        public static readonly string TestNetworkName = "husky-test-network";
        public static readonly string TestImageName = "husky-test-image";
        public static readonly string TestContainerName = "husky-test-runner";
        public static readonly string SeqImageName = "seq-windows";
        public static readonly string SeqContainerName = "husky-test-seq";
        public static readonly string SeqVersion = "2021.1.5307";
        public static readonly string SeqPort = "5341";

        public static bool IsInDockerContainer()
            => Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

        public static string ExecuteDockerCommand(string args, string workingDirectory = null, bool validateSuccess = true)
        {
            var runResult = Cli.Wrap("docker")
                               .WithArguments(args)
                               .WithWorkingDirectory(workingDirectory ?? Directory.GetCurrentDirectory())
                               .WithValidation(validateSuccess ? CommandResultValidation.ZeroExitCode : CommandResultValidation.None)
                               .ExecuteBufferedAsync().GetAwaiter().GetResult();

            Log.Debug($"Ran docker {args} in {runResult.RunTime.TotalMilliseconds}ms with result:\n{runResult.StandardOutput}");

            if (runResult.ExitCode is not 0)
            {
                if (validateSuccess)
                    throw new NUnitEngineException($"Command Execution Failed\n Tried to run {runResult.StandardOutput}\nGot Error {runResult.ExitCode} {runResult.StandardError}");

                Log.Warning($"Command Execution Returned Nonzero Exit Code\n Tried to run {runResult.StandardOutput}\nGot {runResult.ExitCode} {runResult.StandardError}");
            }

            return runResult.StandardOutput;
        }
    }
}