using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UltimateStreamMgr.Launcher
{

    public enum LauncherUpdateSteps
    {
        Downloading = 0,
        Copying = 1
    }

    /// <summary>
    /// Logique d'interaction pour App.xaml
    /// </summary>
    public partial class App : Application
    {

        private string _launcherNewVersion = "";
        private bool _launcherRequireStop = false;

        private string _installPackage = _releasePackage;

        private const string _targetVersionFile = "version.txt";
        private const string _releasePackage = "UltimateStreamManager";
        private const string _betaPackage = "UltimateStreamManager-Beta";

        private readonly string _packageDirectory = Path.Combine(
            Environment.ExpandEnvironmentVariables("%USERPROFILE%"),
            ".nuget",
            "packages"
        );

        private string GetPackageDirectory(string package)
        {
            return Path.Combine(_packageDirectory, package);
        }

        private const string nugetTokenP1 = "a3659f3501ee28d03eae";
        private const string nugetTokenP2 = "d00b887e4b376f8e6803";

        protected override void OnStartup(StartupEventArgs e)
        {
            HandleLauncherUpdate(e);

            if (_launcherRequireStop)
            {
                Shutdown(0);
                return;
            }

            string requestedVersion = "", requestedVersionPackage = "";
            DetermineRequestedVersion(e, ref requestedVersion, ref requestedVersionPackage);

            string[] installedVersion = RetrieveLocalVersion(requestedVersionPackage);
            string[] availableVersion = RetrievePublishedVersion(requestedVersionPackage);

            // If the user is asking for a "latest" version we need to resolve it to an actual version number
            if (string.IsNullOrEmpty(requestedVersion) || requestedVersion == "latest") 
            { 
                if(availableVersion.Length > 0) // We're online !
                {
                    requestedVersion = availableVersion[0];
                }
                else if (availableVersion.Length == 0 && installedVersion.Length == 0) // We're offline and no version is already installed
                {
                    using (new Notification("UltimateStreamManager", $"You need to have access to internet for the first launch !",
                        NotificationType.Error))
                    {
                        Shutdown(1);
                        return;
                    }
                }
                else // We're offline but something is installed, take the latest installed version
                {
                    requestedVersion = installedVersion[0];
                }

                Console.WriteLine($"'latest' version has been resolved to {requestedVersion}");
            }

            if(installedVersion.Contains(requestedVersion)) // We have the version needed already
            {
                // Nothing to do
            }
            else if(availableVersion.Length > 0 && availableVersion.Contains(requestedVersion)) // Connected & the version exists
            {
                Install(requestedVersion, requestedVersionPackage);
            }
            else if (availableVersion.Length > 0 && !availableVersion.Contains(requestedVersion)) // Check if his version actually exists
            {
                using (new Notification("UltimateStreamManager", $"The version you wanna launch does not exists !",
                    NotificationType.Error))
                {
                    Shutdown(1);
                    return;
                }
            }
            else if (availableVersion.Length == 0) // We're offline and we do not have the version
            {
                using (new Notification("UltimateStreamManager", $"You need to have access to internet to download this version !",
                    NotificationType.Error))
                {
                    Shutdown(1);
                    return;
                }
            }

            Start(requestedVersion, requestedVersionPackage);
            Shutdown(0);
        }

        private string[] RetrieveLocalVersion(string package)
        {
            string basePath = GetPackageDirectory(package);

            if (!Directory.Exists(basePath) || Directory.GetDirectories(basePath).Length == 0)
                return new string[0];

            var directories = Directory.EnumerateDirectories(basePath);

            return directories.Select(d => {
                var name = Path.GetFileName(d);
                Console.WriteLine($"Found version {name}");
                return name;
            }).Reverse().ToArray();
        }
        private string[] RetrievePublishedVersion(string package)
        {
            try
            {
                using (var client = new HttpClient(new HttpClientHandler{ AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }))
                {
                    client.BaseAddress = new Uri("https://api.github.com/");
                    HttpRequestMessage request =
                        new HttpRequestMessage(HttpMethod.Post, "/graphql");
                    request.Headers.Add("User-Agent", "USM.Launcher");
                    request.Headers.Add("Authorization", "bearer " + nugetTokenP1 + nugetTokenP2);
                    request.Headers.Add("Accept", "application/vnd.github.packages-preview+json");
                    string graphqlRequest = @"
                     { ""query"": 
                       ""query {
                          repository(owner:\""Tibec\"", name:\""UltimateStreamManager\"") {
                            name
                            packages(last: 3)
                            {
                              nodes {
                                name
                                versions(last:100) {
                                  nodes {
                                    version
                                  }
                                }
                              }
                            }
                           }
                        }""
                     }";
                    request.Content = new StringContent(graphqlRequest.Replace("\r\n", "").Replace("\t", ""));

                    HttpResponseMessage response = client.SendAsync(request).Result;
                    string result = response.Content.ReadAsStringAsync().Result;
                    dynamic json = JsonConvert.DeserializeObject(result);
                    JArray repo = json.data.repository.packages.nodes;
                    JObject repoVersion = repo.First(e => (e as JObject)["name"].ToString() == package).ToObject<JObject>();
                    List<string> releases = new List<string>();

                    foreach (var release in repoVersion["versions"]["nodes"])
                    {
                        string n = release["version"].ToString();
                        Console.WriteLine($"Found published version : {n}");
                        releases.Add(n);
                    }

                    return releases.ToArray();
                }
            }
            catch
            {
                return new string[0];
            }
        }


        /// <summary>
        /// Determine wihch version the user is looking for based on a text file and the commandline (commandline decide over the version.txt file)
        /// <param name="args">Command line args event</param>
        /// <param name="requestedVersion">output the version specified (null if the user want the latest)</param>
        /// <param name="requestedVersionPackage">output the version package repository (beta or official)</param>
        private void DetermineRequestedVersion(StartupEventArgs args, ref string requestedVersion, ref string requestedVersionPackage)
        {
            LoadRequestedVersionFromFile(ref requestedVersion, ref requestedVersionPackage);
            ParseArguments(args, ref requestedVersion, ref requestedVersionPackage);
        }

        private void LoadRequestedVersionFromFile(ref string requestedVersion, ref string requestedVersionPackage)
        {
            if (File.Exists(_targetVersionFile))
            {
                ParseRequestedVersion(File.ReadAllText(_targetVersionFile).Trim(), ref requestedVersion, ref requestedVersionPackage);
            }
        }

        private void ParseRequestedVersion(string rawRequestedVersion, ref string requestedVersion, ref string requestedVersionPackage)
        {
            if (rawRequestedVersion.StartsWith("beta-"))
            {
                requestedVersion = rawRequestedVersion.Substring(5);
                requestedVersionPackage = _betaPackage;
            }
            else
            {
                requestedVersion = rawRequestedVersion;
                requestedVersionPackage = _releasePackage;
            }
        }


        private void ParseArguments(StartupEventArgs e, ref string requestedVersion, ref string requestedVersionPackage)
        {
            if (e.Args.Length == 2)
            {
                if (e.Args[0] == "version")
                {
                    ParseRequestedVersion(e.Args[1], ref requestedVersion, ref requestedVersionPackage);
                }
            }
        }

        #region Launcher Update Flow

        private void HandleLauncherUpdate(StartupEventArgs e)
        {
            if (e.Args.Length == 2 && e.Args[0] == "update")
            {
                LauncherUpdateSteps current = (LauncherUpdateSteps) Enum.Parse(typeof(LauncherUpdateSteps), e.Args[1]);
                PerformUpdate(current);
            }
            else if (LauncherUpdateAvailable())
            {
                PerformUpdate(LauncherUpdateSteps.Downloading);
            }
        }

        private bool LauncherUpdateAvailable()
        {
            try
            {

                using (var client = new HttpClient(new HttpClientHandler
                    {AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate}))
                {
                    client.BaseAddress = new Uri("https://api.github.com/");
                    HttpRequestMessage request =
                        new HttpRequestMessage(HttpMethod.Post, "/graphql");
                    request.Headers.Add("User-Agent", "USM.Launcher");
                    request.Headers.Add("Authorization", "bearer " + nugetTokenP1 + nugetTokenP2);
                    request.Headers.Add("Accept", "application/vnd.github.packages-preview+json");
                    string graphqlRequest = @"
                     { ""query"": 
                       ""query
                        {
                          repository(name:\""UltimateStreamManager.Launcher\"",owner:\""Tibec\"" ) {
		                    packages(names:\""UltimateStreamManager.Launcher\"", first:1) {
                              nodes {
                                versions(first:1) {
                                  nodes {
                                    version
                                  }
                                }
                              }
                            }    
                          }
                        }""}";
                    request.Content = new StringContent(graphqlRequest.Replace("\r\n", "").Replace("\t", ""));

                    HttpResponseMessage response = client.SendAsync(request).Result;
                    string result = response.Content.ReadAsStringAsync().Result;
                    dynamic json = JsonConvert.DeserializeObject(result);
                    JArray repo = json.data.repository.packages.nodes;
                        List<string> releases = new List<string>();
                    if (repo.Count > 0)
                    {
                        JObject repoVersion = repo.First().ToObject<JObject>();

                        foreach (var release in repoVersion["versions"]["nodes"])
                        {
                            string n = release["version"].ToString();
                            releases.Add(n);
                        }

                        releases.Sort();
                    }

                    string latestLauncherVersion = releases.Count == 0 ? "" : releases.Last();
                    string currentLauncherVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
                    Console.WriteLine($"Current Launcher : {currentLauncherVersion} | Latest : {latestLauncherVersion}");

                    _launcherNewVersion = latestLauncherVersion;

                    return new[] { latestLauncherVersion, currentLauncherVersion }.OrderBy(v => v).Last() != currentLauncherVersion;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void PerformUpdate(LauncherUpdateSteps currentSteps)
        {
            switch (currentSteps)
            {
                case LauncherUpdateSteps.Downloading:
                    DownloadUpdate();
                    break;

                case LauncherUpdateSteps.Copying: // Where currently being executed from somewhere
                    ApplyUpdate();
                    break;
            }
        }

        private void ApplyUpdate()
        {
            string copyOutput = Path.Combine(Directory.GetCurrentDirectory(), "UltimateStreamMgr.Launcher.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, copyOutput, true);
            Process process = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    FileName = copyOutput,
                    UseShellExecute = false
                }
            };

            process.Start();
            _launcherRequireStop = true;
        }

        #endregion


        private void Start(string version, string package)
        {
            string exe = Path.Combine(GetPackageDirectory(package), version, "UltimateStreamMgr.exe");
            Process process = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    FileName = exe,
                    UseShellExecute = false
                }
            };

            process.Start();
        }

        private void Install(string requestedVersion, string requestedVersionPackage)
        {
            using (new Notification("UltimateStreamManager", $"Updating to v{requestedVersion} ...", NotificationType.Info))
            {
                string outputDirectory = Path.Combine(Environment.ExpandEnvironmentVariables("%TEMP%"), Path.GetRandomFileName());
                Directory.CreateDirectory(outputDirectory);

                InstallNugetPackage(requestedVersionPackage, requestedVersion, outputDirectory);

                Directory.Delete(outputDirectory, true);
            }

        }

        private void DownloadUpdate()
        {
            using (new Notification("UltimateStreamManager", "Updating launcher ...",
                NotificationType.Info))
            {

                string outputDirectory = Path.Combine(Environment.ExpandEnvironmentVariables("%TEMP%"), Path.GetRandomFileName());
                Directory.CreateDirectory(outputDirectory);

                InstallNugetPackage("UltimateStreamManager.Launcher", _launcherNewVersion, outputDirectory);
                try
                {
                    string updateFolder = Directory.GetDirectories(outputDirectory).First();

                    Process process = new Process
                    {
                        StartInfo =
                        {
                            WorkingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                            FileName = Path.Combine(updateFolder, "UltimateStreamMgr.Launcher.exe"),
                            UseShellExecute = false,
                            Arguments = "update 1"
                        }
                    };

                    process.Start();
                    _launcherRequireStop = true;
                }
                catch (Exception)
                {
                    using (new Notification("UltimateStreamManager", "Error while trying to install the launcher update. We'll run with the current one.",
                        NotificationType.Warning))
                    {
                    }
                }
            }
        }
         
        private void InstallNugetPackage(string packageName, string packageVersion, string outputDirectory)
        {
            // Well i let that in clear, because it's a random account i created specially for this with only repo and package:read rights
            // It should be harmless.. Maybe.
            string removeRepoCommand =
                "sources Remove -Name GPR_USM";
            string installRepoCommand =
                "sources Add -Name GPR_USM -Source https://nuget.pkg.github.com/Tibec/index.json -UserName userbidon42 -Password " +
                nugetTokenP1 + nugetTokenP2;

            NugetUtils.RunCommand(removeRepoCommand);
            NugetUtils.RunCommand(installRepoCommand);

            string installPackageCommand =
                $"install {packageName} -Version {packageVersion} -OutputDirectory {outputDirectory} -NoCache -NonInteractive -source GPR_USM ";

            NugetUtils.RunCommand(installPackageCommand);
        }
    }
}
