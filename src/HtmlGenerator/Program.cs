using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            var projects = new List<string>();
            ISet<string> excludedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var properties = new Dictionary<string, string>();
            var emitAssemblyList = false;
            var force = false;
            var noBuiltInFederations = false;
            var offlineFederations = new Dictionary<string, string>();
            var federations = new HashSet<string>();
            var serverPathMappings = new Dictionary<string, string>();
            var pluginBlacklist = new List<string>();

            foreach (var arg in args)
            {
                if (arg.StartsWith("/out:"))
                {
                    Paths.SolutionDestinationFolder = Path.GetFullPath(arg.Substring("/out:".Length).StripQuotes());
                    continue;
                }

                if (arg.StartsWith("/serverPath:"))
                {
                    var mapping = arg.Substring("/serverPath:".Length).StripQuotes();
                    var parts = mapping.Split('=');
                    if (parts.Length != 2)
                    {
                        Log.Write($"Invalid Server Path: '{mapping}'", ConsoleColor.Red);
                        continue;
                    }
                    serverPathMappings.Add(Path.GetFullPath(parts[0]), parts[1]);
                    continue;
                }

                if (arg == "/force")
                {
                    force = true;
                    continue;
                }

                if (arg.StartsWith("/in:"))
                {
                    string inputPath = arg.Substring("/in:".Length).StripQuotes();
                    try
                    {
                        if (!File.Exists(inputPath))
                        {
                            continue;
                        }

                        string[] paths = File.ReadAllLines(inputPath);
                        foreach (string path in paths)
                        {
                            AddProject(projects, path);
                        }
                    }
                    catch
                    {
                        Log.Write("Invalid argument: " + arg, ConsoleColor.Red);
                    }

                    continue;
                }

                if (arg.StartsWith("/exclude:"))
                {
                    string inputPath = arg.Substring("/exclude:".Length).StripQuotes();
                    try
                    {
                        if (!File.Exists(inputPath))
                        {
                            continue;
                        }

                        string[] projectsNames = File.ReadAllLines(inputPath);
                        foreach (string projectName in projectsNames)
                        {
                            AddExcludedProject(excludedProjects, projectName);
                        }
                    }
                    catch
                    {
                        Log.Write("Invalid argument: " + arg, ConsoleColor.Red);
                    }

                    continue;
                }

                if (arg.StartsWith("/p:"))
                {
                    var match = Regex.Match(arg, "/p:(?<name>[^=]+)=(?<value>.+)");
                    if (match.Success)
                    {
                        var propertyName = match.Groups["name"].Value;
                        var propertyValue = match.Groups["value"].Value;
                        properties.Add(propertyName, propertyValue);
                        continue;
                    }
                }

                if (arg == "/assemblylist")
                {
                    emitAssemblyList = true;
                    continue;
                }

                if (arg == "/nobuiltinfederations")
                {
                    noBuiltInFederations = true;
                    Log.Message("Disabling built-in federations.");
                    continue;
                }

                if (arg.StartsWith("/federation:"))
                {
                    string server = arg.Substring("/federation:".Length);
                    Log.Message($"Adding federation '{server}'.");
                    federations.Add(server);
                    continue;
                }

                if (arg.StartsWith("/offlinefederation:"))
                {
                    var match = Regex.Match(arg, "/offlinefederation:(?<server>[^=]+)=(?<file>.+)");
                    if (match.Success)
                    {
                        var server = match.Groups["server"].Value;
                        var assemblyListFileName = match.Groups["file"].Value;
                        offlineFederations[server] = assemblyListFileName;
                        Log.Message($"Adding federation '{server}' (offline from '{assemblyListFileName}').");
                        continue;
                    }
                    continue;
                }

                if (arg.StartsWith("/fedlist:"))
                {
                    string inputPath = arg.Substring("/fedlist:".Length).StripQuotes();
                    try
                    {
                        if (!File.Exists(inputPath))
                        {
                            continue;
                        }

                        string[] fedList = File.ReadAllLines(inputPath);
                        foreach (string federation in fedList)
                        {
                            var match = Regex.Match(arg, "offline:(?<server>[^=]+)=(?<file>.+)");
                            if (match.Success)
                            {
                                var server = match.Groups["server"].Value;
                                var assemblyListFileName = match.Groups["file"].Value;
                                offlineFederations[server] = assemblyListFileName;
                                Log.Message($"Adding federation '{server}' (offline from '{assemblyListFileName}').");
                            }
                            else
                            {
                                Log.Message($"Adding federation '{federation}'.");
                                federations.Add(federation);
                            }
                        }
                    }
                    catch
                    {
                        Log.Write("Invalid argument: " + arg, ConsoleColor.Red);
                    }

                    continue;
                }

                if (string.Equals(arg, "/noplugins", StringComparison.OrdinalIgnoreCase))
                {
                    SolutionGenerator.LoadPlugins = false;
                    continue;
                }

                if (arg.StartsWith("/noplugin:"))
                {
                    pluginBlacklist.Add(arg.Substring("/noplugin:".Length));
                    continue;
                }

                try
                {
                    AddProject(projects, arg);
                }
                catch (Exception ex)
                {
                    Log.Write("Exception: " + ex.ToString(), ConsoleColor.Red);
                }
            }

            if (projects.Count == 0)
            {
                PrintUsage();
                return;
            }

            AssertTraceListener.Register();
            AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler.HandleFirstChanceException;

            // This loads the real MSBuild from the toolset so that all targets and SDKs can be found
            // as if a real build is happening
            MSBuildLocator.RegisterDefaults();

            if (Paths.SolutionDestinationFolder == null)
            {
                Paths.SolutionDestinationFolder = Path.Combine(Microsoft.SourceBrowser.Common.Paths.BaseAppFolder, "Index");
            }

            var websiteDestination = Paths.SolutionDestinationFolder;

            // Warning, this will delete and recreate your destination folder
            Paths.PrepareDestinationFolder(force);

            Paths.SolutionDestinationFolder = Path.Combine(Paths.SolutionDestinationFolder, "index"); //The actual index files need to be written to the "index" subdirectory

            Directory.CreateDirectory(Paths.SolutionDestinationFolder);

            Log.ErrorLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.ErrorLogFile);
            Log.MessageLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.MessageLogFile);

            using (Disposable.Timing("Generating website"))
            {
                var federation = new Federation();

                federation.AddFederations(federations);

                foreach (var entry in offlineFederations)
                {
                    federation.AddFederation(entry.Key, entry.Value);
                }

                if (!noBuiltInFederations)
                {
                    federation.AddFederations(Federation.DefaultFederatedIndexUrls);
                }

                IndexSolutions(projects, excludedProjects, properties, federation, serverPathMappings, pluginBlacklist);
                FinalizeProjects(emitAssemblyList, excludedProjects, federation);
                WebsiteFinalizer.Finalize(websiteDestination, emitAssemblyList, federation);
            }
        }

        private static void AddProject(List<string> projects, string path)
        {
            var project = Path.GetFullPath(path);
            if (IsSupportedProject(project))
            {
                projects.Add(project);
            }
            else
            {
                Log.Exception("Project not found or not supported: " + path, isSevere: false);
            }
        }

        private static bool IsSupportedProject(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            return filePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddExcludedProject(ISet<string> excludedProjects, string projectName)
        {
            excludedProjects.Add(projectName);
        }

        private static bool IsExludedProject(ISet<string> excludedProjects, string projectPath)
        {
            string projectName = Path.GetFileName(projectPath);
            return excludedProjects.Contains(projectName);
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: HtmlGenerator "
                + @"[/out:<outputdirectory>] "
                + @"[/force] "
                + @"[/noplugins] "
                + @"[/noplugin:Git] "
                + @"<pathtosolution1.csproj|vbproj|sln> [more solutions/projects..] "
                + @"[/in:<file>] "
                + @"[/exclude:<file>] "
                + @"[/nobuiltinfederations] "
                + @"[/offlinefederation:server=assemblyListFile] "
                + @"[/assemblylist] "
                + @"[/federation:<serverUrl>] "
                + @"[/offlinefederation:<serverUrl>=<offlineFilePath>] "
                + @"[/fedlist:<file>]");
        }

        private static readonly Folder<Project> mergedSolutionExplorerRoot = new Folder<Project>();

        private static void IndexSolutions(IEnumerable<string> solutionFilePaths, ISet<string> excludedProjects, Dictionary<string, string> properties, Federation federation, Dictionary<string, string> serverPathMappings, IEnumerable<string> pluginBlacklist)
        {
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Reading assembly names from " + path))
                {
                    if(IsExludedProject(excludedProjects, path))
                    {
                        Log.Write("The project/solution is excluded: " + path, ConsoleColor.Yellow);
                        continue;
                    }

                    foreach (var assemblyName in AssemblyNameExtractor.GetAssemblyNames(path, excludedProjects))
                    {
                        assemblyNames.Add(assemblyName);
                        Log.Write("Assembly to process: " + assemblyName, ConsoleColor.Gray);
                    }
                }
            }

            var processedAssemblyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Generating " + path))
                {
                    if (IsExludedProject(excludedProjects, path))
                    {
                        Log.Write("The project/solution is excluded: " + path, ConsoleColor.Yellow);
                        continue;
                    }

                    using (var solutionGenerator = new SolutionGenerator(
                        path,
                        Paths.SolutionDestinationFolder,
                        properties: properties.ToImmutableDictionary(),
                        federation: federation,
                        serverPathMappings: serverPathMappings,
                        pluginBlacklist: pluginBlacklist))
                    {
                        solutionGenerator.GlobalAssemblyList = assemblyNames;
                        solutionGenerator.Generate(processedAssemblyList, mergedSolutionExplorerRoot);
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private static void FinalizeProjects(bool emitAssemblyList, ISet<string> excludedProjects, Federation federation)
        {
            GenerateLooseFilesProject(Constants.MSBuildFiles, Paths.SolutionDestinationFolder);
            GenerateLooseFilesProject(Constants.TypeScriptFiles, Paths.SolutionDestinationFolder);
            using (Disposable.Timing("Finalizing references"))
            {
                try
                {
                    var solutionFinalizer = new SolutionFinalizer(Paths.SolutionDestinationFolder);
                    solutionFinalizer.FinalizeProjects(emitAssemblyList, excludedProjects, federation, mergedSolutionExplorerRoot);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, "Failure while finalizing projects");
                }
            }
        }

        private static void GenerateLooseFilesProject(string projectName, string solutionDestinationPath)
        {
            var projectGenerator = new ProjectGenerator(projectName, solutionDestinationPath);
            projectGenerator.GenerateNonProjectFolder();
        }
    }

    internal static class WebsiteFinalizer
    {
        public static void Finalize(string destinationFolder, bool emitAssemblyList, Federation federation)
        {
            string sourcePath = Assembly.GetEntryAssembly().Location;
            sourcePath = Path.GetDirectoryName(sourcePath);
            string basePath = sourcePath;
            sourcePath = Path.Combine(sourcePath, @"Web");
            if (!Directory.Exists(sourcePath))
            {
                return;
            }

            sourcePath = Path.GetFullPath(sourcePath);
            FileUtilities.CopyDirectory(sourcePath, destinationFolder);

            StampOverviewHtmlWithDate(destinationFolder);

            if (emitAssemblyList)
            {
                ToggleSolutionExplorerOff(destinationFolder);
            }

            SetExternalUrlMap(destinationFolder, federation);
        }

        private static void StampOverviewHtmlWithDate(string destinationFolder)
        {
            var source = Path.Combine(destinationFolder, "wwwroot", "overview.html");
            var dst = Path.Combine(destinationFolder, "index", "overview.html");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = StampOverviewHtmlText(text);
                File.WriteAllText(dst, text);
            }
        }

        private static string StampOverviewHtmlText(string text)
        {
            text = text.Replace("$(Date)", DateTime.Today.ToString("MMMM d", CultureInfo.InvariantCulture));
            return text;
        }

        private static void ToggleSolutionExplorerOff(string destinationFolder)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = text.Replace("/*USE_SOLUTION_EXPLORER*/true/*USE_SOLUTION_EXPLORER*/", "false");
                File.WriteAllText(dst, text);
            }
        }

        private static void SetExternalUrlMap(string destinationFolder, Federation federation)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
                var sb = new StringBuilder();
                foreach (var server in federation.GetServers())
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(",");
                    }

                    sb.Append("\"");
                    sb.Append(server);
                    sb.Append("\"");
                }

                if (sb.Length > 0)
                {
                    var text = File.ReadAllText(source);
                    text = Regex.Replace(text, @"/\*EXTERNAL_URL_MAP\*/.*/\*EXTERNAL_URL_MAP\*/", sb.ToString());
                    File.WriteAllText(dst, text);
                }
            }
        }
    }
}
