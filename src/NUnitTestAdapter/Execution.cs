using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using CliWrap;
using CliWrap.Buffered;
using NUnit.Engine;
using NUnit.VisualStudio.TestAdapter.Dump;
using NUnit.VisualStudio.TestAdapter.Internal;
using NUnit.VisualStudio.TestAdapter.NUnitEngine;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.SystemConsole.Themes;

namespace NUnit.VisualStudio.TestAdapter
{
    public interface IExecutionContext
    {
        ITestLogger Log { get; }
        INUnitEngineAdapter EngineAdapter { get; }
        string TestOutputXmlFolder { get; }
        IAdapterSettings Settings { get; }
        IDumpXml Dump { get; }

        IVsTestFilter VsTestFilter { get; }
    }

    public static class ExecutionFactory
    {
        public static Execution Create(IExecutionContext ctx)
        {
            if (!Docker.IsInDockerContainer())
                return new DockerExecution(ctx);
            if (ctx.Settings.DesignMode) // We come from IDE
                return new IdeExecution(ctx);
            return new VsTestExecution(ctx);
        }
    }

    public abstract class Execution
    {
        protected string TestOutputXmlFolder => ctx.TestOutputXmlFolder;
        private readonly IExecutionContext ctx;
        protected ITestLogger TestLog => ctx.Log;
        protected IAdapterSettings Settings => ctx.Settings;

        protected IDumpXml Dump => ctx.Dump;
        protected IVsTestFilter VsTestFilter => ctx.VsTestFilter;

        protected INUnitEngineAdapter NUnitEngineAdapter => ctx.EngineAdapter;
        protected Execution(IExecutionContext ctx)
        {
            this.ctx = ctx;
        }



        public virtual bool Run(TestFilter filter, DiscoveryConverter discovery, NUnit3TestExecutor nUnit3TestExecutor)
        {
            filter = CheckFilterInCurrentMode(filter, discovery);
            nUnit3TestExecutor.Dump?.StartExecution(filter, "(At Execution)");
            var converter = CreateConverter(discovery);
            using var listener = new NUnitEventListener(converter, nUnit3TestExecutor);
            try
            {
                var results = NUnitEngineAdapter.Run(listener, filter);
                NUnitEngineAdapter.GenerateTestOutput(results, discovery.AssemblyPath, TestOutputXmlFolder);
            }
            catch (NullReferenceException)
            {
                // this happens during the run when CancelRun is called.
                TestLog.Debug("   Null ref caught");
            }

            return true;
        }

        public abstract TestFilter CheckFilterInCurrentMode(TestFilter filter, IDiscoveryConverter discovery);

        protected NUnitTestFilterBuilder CreateTestFilterBuilder()
            => new NUnitTestFilterBuilder(NUnitEngineAdapter.GetService<ITestFilterService>(), Settings);
        protected ITestConverterCommon CreateConverter(DiscoveryConverter discovery) => Settings.DiscoveryMethod == DiscoveryMethod.Current ? discovery.TestConverter : discovery.TestConverterForXml;

        protected TestFilter CheckFilter(IDiscoveryConverter discovery)
        {
            TestFilter filter;
            if (discovery.NoOfLoadedTestCasesAboveLimit)
            {
                TestLog.Debug("Setting filter to empty due to number of testcases");
                filter = TestFilter.Empty;
            }
            else
            {
                var filterBuilder = CreateTestFilterBuilder();
                filter = filterBuilder.FilterByList(discovery.LoadedTestCases);
            }
            return filter;
        }
    }

    public class DockerExecution : Execution
    {
        public DockerExecution(IExecutionContext ctx) : base(ctx)
        {
        }

        public override bool Run(TestFilter filter, DiscoveryConverter discovery, NUnit3TestExecutor nUnit3TestExecutor)
        {
            var assemblyDirectory = Path.GetDirectoryName(discovery.AssemblyPath);
            var assemblyName = Path.GetFileName(discovery.AssemblyPath);
            var nunitResultsDir_host = Directory.CreateDirectory(Path.Combine(assemblyDirectory, "husky_test_results"));

            BuildTestImage(assemblyDirectory, Docker.TestImageName);

            var sw = Stopwatch.StartNew();
            RunTestContainer(assemblyDirectory, assemblyName, nunitResultsDir_host.FullName, Docker.TestContainerName, Docker.TestNetworkName, discovery.AllTestCases.Select(s => s.FullName));
            var testExecutionTimeMs = sw.ElapsedMilliseconds;
            var testResult = nunitResultsDir_host.EnumerateFiles().First();
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(System.IO.File.ReadAllText(testResult.FullName));
            var testCaseResults = xmlDoc.SelectNodes("//test-case").OfType<XmlNode>().Select(s => new NUnitTestEventTestCase(s)).ToArray();
            SeriLogger.Logger.Information("Executed {testCasesExecuted} test(s) in {timeTaken}ms", testCaseResults.Length, testExecutionTimeMs);
            var outputNodes = new List<INUnitTestEventTestOutput>();
            foreach (var test in testCaseResults)
            {
                var convertedCase = discovery.TestConverter.GetVsTestResults(test, outputNodes);
                SeriLogger.Logger.Debug("Recording {testName} as executed in {timeTaken}ms with result {result}", test.Name, test.Duration.TotalMilliseconds.ToString("N"), test.Result().ToString());
                nUnit3TestExecutor.FrameworkHandle.RecordResult(convertedCase.TestCaseResult);
            }

            testResult.Delete();
            return true;
        }

        public override TestFilter CheckFilterInCurrentMode(TestFilter filter, IDiscoveryConverter discovery) => filter; // Assume current filter is good

        private void BuildTestImage(string dockerFileDirectory, string imageName)
            => _ = Docker.ExecuteDockerCommand($"build -t {imageName} .", dockerFileDirectory);

        private void RunTestContainer(string assemblyDirectory, string assemblyName, string testResultsDirectory, string containerName, string networkName, IEnumerable<string> testCaseNames)
        {
            var testResultsFolder = "husky_test_results";
            var nunitResultsDir_container = Directory.CreateDirectory(Path.Combine("C:/", testResultsFolder));
            var testsToRun = new StringBuilder().Append("FullyQualifiedName=").AppendJoin("|FullyQualifiedName=", testCaseNames);
            var dockerCommand =
                $"run" +
                $" --rm" +
                $" --name {containerName}" +
                $" --network {networkName}" +
                $" -v \"{testResultsDirectory}:{nunitResultsDir_container}\"" +
                $" {Docker.TestImageName} {assemblyName}" +
                $" --filter \"{testsToRun}\"" +
                $" -- NUnit.TestOutputXml={nunitResultsDir_container}";

            _ = Docker.ExecuteDockerCommand(dockerCommand, assemblyDirectory, false);
        }
    }

    public class IdeExecution : Execution
    {
        public IdeExecution(IExecutionContext ctx) : base(ctx)
        {
        }
        public override bool Run(TestFilter filter, DiscoveryConverter discovery, NUnit3TestExecutor nUnit3TestExecutor)
        {
            return base.Run(filter, discovery, nUnit3TestExecutor);
        }

        public override TestFilter CheckFilterInCurrentMode(TestFilter filter, IDiscoveryConverter discovery)
        {
            if (!discovery.IsDiscoveryMethodCurrent)
                return filter;
            if (filter.IsEmpty())
                return filter;
            filter = CheckFilter(discovery);
            return filter;
        }
    }

    public class VsTestExecution : Execution
    {
        public VsTestExecution(IExecutionContext ctx) : base(ctx)
        {
        }

        public override bool Run(TestFilter filter, DiscoveryConverter discovery, NUnit3TestExecutor nUnit3TestExecutor)
        {
            filter = CheckVsTestFilter(filter, discovery, VsTestFilter);

            if (filter == NUnitTestFilterBuilder.NoTestsFound)
            {
                TestLog.Info("   Skipping assembly - no matching test cases found");
                return false;
            }
            return base.Run(filter, discovery, nUnit3TestExecutor);
        }

        public TestFilter CheckVsTestFilter(TestFilter filter, IDiscoveryConverter discovery, IVsTestFilter vsTestFilter)
        {
            // If we have a VSTest TestFilter, convert it to an nunit filter
            if (vsTestFilter == null || vsTestFilter.IsEmpty)
                return filter;
            TestLog.Debug(
                $"TfsFilter used, length: {vsTestFilter.TfsTestCaseFilterExpression?.TestCaseFilterValue.Length}");
            // NOTE This overwrites filter used in call
            var filterBuilder = CreateTestFilterBuilder();
            if (Settings.DiscoveryMethod == DiscoveryMethod.Current)
            {
                filter = Settings.UseNUnitFilter
                    ? filterBuilder.ConvertVsTestFilterToNUnitFilter(vsTestFilter)
                    : filterBuilder.ConvertTfsFilterToNUnitFilter(vsTestFilter, discovery);
            }
            else
            {
                filter = filterBuilder
                    .ConvertTfsFilterToNUnitFilter(vsTestFilter, discovery.LoadedTestCases);
            }

            Dump?.AddString($"\n\nTFSFilter: {vsTestFilter.TfsTestCaseFilterExpression.TestCaseFilterValue}\n");
            Dump?.DumpVSInputFilter(filter, "(At Execution (TfsFilter)");

            return filter;
        }
        public override TestFilter CheckFilterInCurrentMode(TestFilter filter, IDiscoveryConverter discovery)
        {
            if (!discovery.IsDiscoveryMethodCurrent)
                return filter;
            if ((VsTestFilter == null || VsTestFilter.IsEmpty) && filter != TestFilter.Empty)
            {
                filter = CheckFilter(discovery);
            }
            else if (VsTestFilter != null && !VsTestFilter.IsEmpty && !Settings.UseNUnitFilter)
            {
                var s = VsTestFilter.TfsTestCaseFilterExpression.TestCaseFilterValue;
                var scount = s.Split('|', '&').Length;
                if (scount > Settings.AssemblySelectLimit)
                {
                    TestLog.Debug("Setting filter to empty due to TfsFilter size");
                    filter = TestFilter.Empty;
                }
            }

            return filter;
        }
    }
}
