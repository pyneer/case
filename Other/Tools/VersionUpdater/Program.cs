﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Diagnostics;

namespace VersionUpdater
{
	public class Program
	{
		private static readonly string PackageUpdateCommitTitle = "Package Update";
		private static readonly string PackageUpdateCommitMessageBegin = "#CHANGE: Updated Package Specs to ";
		private static readonly string PackageUpdateCommitMessage = PackageUpdateCommitMessageBegin + "{0} {1}";

		private class ProjectInfo
		{
			public string ProjectFilePath;
			public string ProjectRootDir;
			public string AssemblyInfoFilePath;
			public string NuSpecFilePath;
			public string NuSpecPackageId;
			public Version NuSpecVersion;

			public override string ToString()
			{
				return string.Format("{0} {1}", this.NuSpecPackageId, this.NuSpecVersion);
			}
		}
		private class GitCommitInfo
		{
			public string Id;
			public string Message;
			public string Title;
			public List<string> FilePaths;

			public GitCommitInfo()
			{
				this.FilePaths = new List<string>();
			}
			public override string ToString()
			{
				return string.Format("{0} {1}: {2} changed files", this.Id, this.Title, this.FilePaths.Count);
			}
		}
		private class ProjectChangeInfo
		{
			public ProjectInfo Project;
			public List<string> Titles;
			public List<string> ChangeLog;
			public HashSet<string> ModifiedFilePaths;
			public UpdateMode UpdateMode;

			public ProjectChangeInfo()
			{
				this.Titles = new List<string>();
				this.ChangeLog = new List<string>();
				this.ModifiedFilePaths = new HashSet<string>();
			}
			public override string ToString()
			{
				return string.Format("{0}: {1} changed files", this.Titles.FirstOrDefault(), this.ModifiedFilePaths.Count);
			}
		}
		private enum UpdateMode
		{
			None,

			Patch,
			Minor,
			Major
		}

		public static void Main(string[] args)
		{
			string configFileName = "VersionUpdaterConfig.xml";
			string configFileDir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).AbsolutePath);
			string configFilePath = Path.Combine(configFileDir, configFileName);
			ConfigFile config = ConfigFile.Load(configFilePath);
			
			HashSet<PropertyInfo> configOverride;
			string gitPath;
			List<ProjectInfo> allProjects;
			List<GitCommitInfo> gitHistory;
			List<ProjectChangeInfo> changes;

			// Parse command line arguments and use them to override config properties
			configOverride = ParseCommandLine(config, args);
			string solutionDir = Path.GetDirectoryName(config.SolutionPath);

			// Write some diagnostic data to the log
			{
				Console.WriteLine("VersionUpdater launched");
				Console.WriteLine("Working Dir: {0}", Environment.CurrentDirectory);
				Console.WriteLine("Relative Base Dir: {0}", configFileDir);
				Console.WriteLine("Command Line: {0}", args.Aggregate("", (acc, arg) => acc + " " + arg));
				Console.WriteLine("Config:");
				foreach (PropertyInfo prop in typeof(ConfigFile).GetProperties())
				{
					if (configOverride.Contains(prop))
						Console.ForegroundColor = ConsoleColor.White;
					Console.WriteLine("  {0}: {1}", prop.Name, prop.GetValue(config, null));
					Console.ForegroundColor = ConsoleColor.Gray;
				}
				Console.WriteLine();
				Console.WriteLine();
			}

			// Update all config file paths to be absolute and account for the fact that 
			// the working directory might be different from the one where executable and
			// config file are located
			UpdateConfigPaths(config, configFileDir);

			// Determine the path of a usable git executable
			gitPath = SearchGitPath(config);

			Console.WriteLine("Git executable path: '{0}'", gitPath);
			Console.WriteLine();

			// Check if there are any local changes in the git repo
			{
				bool anyModifiedFiles = false;
				ProcessStartInfo gitStartInfo = new ProcessStartInfo
				{
					FileName = gitPath,
					WorkingDirectory = solutionDir,
					Arguments = "--no-pager ls-files -m",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					CreateNoWindow = true,
					WindowStyle = ProcessWindowStyle.Hidden
				};
				Process gitProc = new Process();
				gitProc.StartInfo = gitStartInfo;
				gitProc.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
				{
					// If we receive any non-empty lines, those are modified files
					if (!string.IsNullOrEmpty(e.Data))
					{
						anyModifiedFiles = true;
						gitProc.CancelOutputRead();
						if (!gitProc.HasExited)
							gitProc.Kill();
						return;
					}
				};
				gitProc.Start();
				gitProc.BeginOutputReadLine();
				gitProc.WaitForExit();

				if (anyModifiedFiles)
				{
					Console.WriteLine();
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine("  There are modified files in your local git repository. Please make sure to start the version updater tool only with a clean working copy and no staged files.");
					Console.ResetColor();
					Console.WriteLine();
					return;
				}
			}

			// Retrieve information about all project files and their nuspec associations
			allProjects = ParseProjectInfo(config);

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("Found {0} projects:", allProjects.Count);
			Console.ResetColor();
			Console.WriteLine();
			int maxProjectNameLen = allProjects.Max(p => p.NuSpecPackageId.Length - p.NuSpecPackageId.LastIndexOf('.') - 1);
			foreach (ProjectInfo project in allProjects)
			{
				string[] nameTokens = project.NuSpecPackageId.Split('.');
				string displayedProjectName = nameTokens.LastOrDefault();

				Console.Write("  {0}", displayedProjectName.PadRight(maxProjectNameLen, ' '));
				Console.Write("{0,7}", project.NuSpecVersion);
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.WriteLine(" in '{0}'", GetRelativePath(project.ProjectRootDir, solutionDir));
				Console.ResetColor();
			}
			Console.WriteLine();

			// Retrieve information about the git history since we last did a package update
			gitHistory = ParseGitCommitsSinceLastPackage(config, gitPath);

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("Logged {0} commits since last package update:", gitHistory.Count);
			Console.ResetColor();
			Console.WriteLine();
			foreach (GitCommitInfo commit in gitHistory)
			{
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write("  {0}: ", commit.Id);
				Console.ResetColor();
				Console.Write("'{0}', ", commit.Title);
				Console.ForegroundColor = ConsoleColor.Blue;
				Console.WriteLine("{0} files changed", commit.FilePaths.Count);
				Console.ResetColor();
			}
			Console.WriteLine();

			// Determine the changes for each project individually and gather changelog entries
			changes = GetChangesPerProject(config, allProjects, gitHistory);

			// Display each change to the user and ask whether it should be considered a patch, minor or major release
			RetrieveUpdateModes(config, changes);

			// Apply the specified update modes to the version numbers of each project
			UpdateVersionNumbers(config, changes);

			// Remove change entries without an update
			changes.RemoveAll(c => c.UpdateMode == UpdateMode.None);

			// Apply version numbers and change logs to nuspecs and projects
			foreach (ProjectChangeInfo changeInfo in changes)
			{
				string versionString = string.Format("{0}.{1}.{2}", 
					changeInfo.Project.NuSpecVersion.Major,
					changeInfo.Project.NuSpecVersion.Minor,
					changeInfo.Project.NuSpecVersion.Build);

				// Update AssemblyInfo version
				{
					string[] assemblyInfoLines = File.ReadAllLines(changeInfo.Project.AssemblyInfoFilePath);
					for (int i = 0; i < assemblyInfoLines.Length; i++)
					{
						string line = assemblyInfoLines[i];
						if (!line.Contains("AssemblyVersion") && !line.Contains("AssemblyFileVersion")) continue;

						int beginIndex = line.IndexOf('(');
						int endIndex = line.IndexOf(')');
						if (beginIndex == -1) continue;
						if (endIndex == -1) continue;

						line = 
							line.Substring(0, beginIndex) + 
							"(\"" + versionString + "\")" +
							line.Substring(endIndex + 1, line.Length - endIndex - 1);
						assemblyInfoLines[i] = line;
					}
					File.WriteAllLines(changeInfo.Project.AssemblyInfoFilePath, assemblyInfoLines);
				}

				// Update nuspec version, release notes and dependencies
				{
					XDocument nuspecDoc = XDocument.Load(changeInfo.Project.NuSpecFilePath);
					XElement metadataElement = nuspecDoc.Descendants("metadata").FirstOrDefault();
					XElement versionElement = metadataElement.Descendants("version").FirstOrDefault();
					XElement releaseNotesElement = metadataElement.Descendants("releaseNotes").FirstOrDefault();
					XElement dependenciesElement = metadataElement.Descendants("dependencies").FirstOrDefault();
					if (releaseNotesElement == null)
					{
						releaseNotesElement = new XElement("releaseNotes");
						metadataElement.Add(releaseNotesElement);
					}

					versionElement.Value = versionString;
					releaseNotesElement.Value = 
						string.Join(", ", changeInfo.Titles.Take(3)) + 
						Environment.NewLine + 
						string.Join(Environment.NewLine, changeInfo.ChangeLog);
					foreach (XElement dependencyElement in dependenciesElement.Descendants("dependency"))
					{
						XAttribute idAttrib = dependencyElement.Attribute("id");
						XAttribute versionAttrib = dependencyElement.Attribute("version");
						if (idAttrib == null) continue;
						if (versionAttrib == null) continue;

						ProjectInfo dependency = allProjects.FirstOrDefault(p => string.Equals(p.NuSpecPackageId, idAttrib.Value, StringComparison.InvariantCultureIgnoreCase));
						if (dependency == null) continue;

						versionAttrib.Value = string.Format("{0}.{1}.{2}", 
							dependency.NuSpecVersion.Major,
							dependency.NuSpecVersion.Minor,
							dependency.NuSpecVersion.Build);
					}

					nuspecDoc.Save(changeInfo.Project.NuSpecFilePath);
				}
			}

			// Perform a git commit with an auto-generated message
			if (changes.Count > 0)
			{
				Console.WriteLine();
				Console.ForegroundColor = ConsoleColor.White;
				Console.WriteLine("Performing Git Commit...");
				Console.ResetColor();
				Console.WriteLine();

				string commitMsgFileName = "PackageUpdateCommitMsg.txt";
				string commitMsgFilePath = Path.GetFullPath(Path.Combine(solutionDir, commitMsgFileName));

				// Build the commit message
				StringBuilder messageBuilder = new StringBuilder();
				messageBuilder.AppendLine(PackageUpdateCommitTitle);
				foreach (ProjectChangeInfo changeInfo in changes)
				{
					string versionString = string.Format("{0}.{1}.{2}", 
						changeInfo.Project.NuSpecVersion.Major,
						changeInfo.Project.NuSpecVersion.Minor,
						changeInfo.Project.NuSpecVersion.Build);
					messageBuilder.AppendFormat(PackageUpdateCommitMessage, changeInfo.Project.NuSpecPackageId, versionString);
					messageBuilder.AppendLine();
				}
				File.WriteAllText(commitMsgFilePath, messageBuilder.ToString());

				// Stage all files in git
				{
					ProcessStartInfo gitStartInfo = new ProcessStartInfo
					{
						FileName = gitPath,
						WorkingDirectory = solutionDir,
						Arguments = "add -u",
						UseShellExecute = false,
					};
					Process gitProc = Process.Start(gitStartInfo);
					gitProc.WaitForExit();
				}

				// Execute a git commit
				{
					ProcessStartInfo gitStartInfo = new ProcessStartInfo
					{
						FileName = gitPath,
						WorkingDirectory = solutionDir,
						Arguments = "commit -F " + commitMsgFileName,
						UseShellExecute = false,
					};
					Process gitProc = Process.Start(gitStartInfo);
					gitProc.WaitForExit();
				}

				// Remove our temporary commit message file
				File.Delete(commitMsgFilePath);
			}

			Console.WriteLine();
			Console.WriteLine();
			Console.WriteLine("All done!");
			Console.WriteLine();
		}

		private static HashSet<PropertyInfo> ParseCommandLine(ConfigFile config, string[] args)
		{
			PropertyInfo[] configProps = typeof(ConfigFile).GetProperties();
			HashSet<PropertyInfo> configOverride = new HashSet<PropertyInfo>();

			foreach (string arg in args)
			{
				string[] token = arg.Split('=');
				if (token.Length != 2) continue;

				PropertyInfo prop = configProps.FirstOrDefault(p => p.Name.ToLower() == token[0].Trim().ToLower());
				if (prop == null) continue;

				object value = null;
				try
				{
					value = Convert.ChangeType(token[1].Trim(), prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
				}
				catch {}
				if (value == null) continue;

				prop.SetValue(config, value, null);
				configOverride.Add(prop);
			}

			return configOverride;
		}
		private static void UpdateConfigPaths(ConfigFile config, string relativeBaseDir)
		{
			string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			config.NuSpecRootDir = GetAbsoluteConfigPath(relativeBaseDir, config.NuSpecRootDir);
			config.SolutionPath = GetAbsoluteConfigPath(relativeBaseDir, config.SolutionPath);
			for (int i = 0; i < config.GitSearchPaths.Count; i++)
			{
				config.GitSearchPaths[i] = config.GitSearchPaths[i].Replace("{LocalAppData}", localAppDataPath);
				config.GitSearchPaths[i] = GetAbsoluteConfigPath(relativeBaseDir, config.GitSearchPaths[i]);
			}
		}
		private static string SearchGitPath(ConfigFile config)
		{
			foreach (string dir in config.GitSearchPaths)
			{
				string localGitPath = Directory.EnumerateFiles(dir, "git.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
				if (localGitPath != null)
					return localGitPath;
			}
			return null;
		}
		private static List<ProjectInfo> ParseProjectInfo(ConfigFile config)
		{
			List<ProjectInfo> projectInfoList = new List<ProjectInfo>();
			string solutionDir = Path.GetDirectoryName(config.SolutionPath);

			// Read all currently existing nuspec files to match them with project files
			Dictionary<string,XDocument> nuspecFiles = new Dictionary<string,XDocument>();
			foreach (string nuspecFile in Directory.EnumerateFiles(config.NuSpecRootDir, "*.nuspec", SearchOption.TopDirectoryOnly))
			{
				XDocument doc = XDocument.Load(nuspecFile);
				nuspecFiles.Add(nuspecFile, doc);
			}

			// Scan the solutions directory tree for projects
			foreach (string projectFile in Directory.EnumerateFiles(solutionDir, "*.csproj", SearchOption.AllDirectories))
			{
				ProjectInfo info = new ProjectInfo
				{
					ProjectFilePath = projectFile,
					ProjectRootDir = Path.GetDirectoryName(projectFile)
				};

				// Determine the project's AssemblyInfo file
				info.AssemblyInfoFilePath = Directory.EnumerateFiles(info.ProjectRootDir, "AssemblyInfo.cs", SearchOption.AllDirectories)
					.OrderBy(path => GetPathDepth(path))
					.FirstOrDefault();

				// Determine which nuspec belongs to this project
				foreach (var pair in nuspecFiles)
				{
					string nuspecFilePath = pair.Key;
					string nuspecFileDir = Path.GetDirectoryName(nuspecFilePath);
					XDocument nuspecDoc = pair.Value;

					// Does it reference any file from this projects directory structure?
					bool anyFileInProjectRoot = false;
					foreach (XElement fileElement in nuspecDoc.Descendants("file"))
					{
						XAttribute srcAttrib = fileElement.Attribute("src");
						if (srcAttrib == null) continue;

						string relativePath = srcAttrib.Value;
						string relativePathWithoutWildcards = relativePath.Split('*')[0];
						string filePathBase = Path.Combine(nuspecFileDir, relativePathWithoutWildcards);
						if (IsPathLocatedIn(filePathBase, info.ProjectRootDir))
						{
							anyFileInProjectRoot = true;
							break;
						}
					}
					if (!anyFileInProjectRoot) continue;

					// If it does, it probably belongs to this project. Retrieve some data and stop searching.
					XElement elemId = nuspecDoc.Descendants("id").FirstOrDefault();
					XElement elemVersion = nuspecDoc.Descendants("version").FirstOrDefault();
					info.NuSpecFilePath = nuspecFilePath;
					info.NuSpecPackageId = elemId.Value.Trim();
					info.NuSpecVersion = Version.Parse(elemVersion.Value.Trim());
					break;
				}

				// If we failed to associate the project with a NuSpec, discard it
				if (info.NuSpecPackageId == null) continue;
				if (info.NuSpecVersion == null) continue;

				// Otherwise, keep it for later
				projectInfoList.Add(info);
			}

			projectInfoList.Sort((a, b) => string.Compare(a.NuSpecPackageId, b.NuSpecPackageId));
			return projectInfoList;
		}
		private static List<GitCommitInfo> ParseGitCommitsSinceLastPackage(ConfigFile config, string gitPath)
		{
			List<GitCommitInfo> commitsSinceLastPackage = new List<GitCommitInfo>();
			string solutionDir = Path.GetDirectoryName(config.SolutionPath);

			// Retrieve the git history for the repository
			ProcessStartInfo gitStartInfo = new ProcessStartInfo
			{
				FileName = gitPath,
				WorkingDirectory = solutionDir,
				Arguments = "--no-pager log --name-only --oneline --since=\"2015\"",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			Process gitProc = new Process();
			gitProc.StartInfo = gitStartInfo;
			gitProc.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
			{
				if (string.IsNullOrEmpty(e.Data)) return;

				// Begin of a new commit
				if (e.Data.Contains(' '))
				{
					string line = e.Data.Trim();
					int indexOfSeparator = line.IndexOf(' ');
					string commitId = line.Substring(0, indexOfSeparator);

					if (Regex.IsMatch(commitId, @"\b[0-9a-f]{5,40}\b"))
					{
						string commitTitleAndMessage = line.Substring(indexOfSeparator, line.Length - indexOfSeparator).Trim();
						string[] commitMessageToken = commitTitleAndMessage.Split('#');
						string commitTitle;
						string commitMessage;

						// Received the expected commit message format in the form
						//
						// Title / Headline
						// #CHANGE: Description
						// #ADD: More Description
						// ...
						if (commitMessageToken.Length > 1)
						{
							commitTitle = commitMessageToken[0].Trim();
							commitMessage = "#" + string.Join(Environment.NewLine + "#", commitMessageToken, 1, commitMessageToken.Length - 1);
						}
						// Received an unexpected message format
						else
						{
							commitMessageToken = commitTitleAndMessage.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
							commitTitle = commitMessageToken[0];
							commitMessage = string.Empty;
						}

						// Stop reading as soon as we see a package update commit
						if (string.Equals(commitTitle, PackageUpdateCommitTitle, StringComparison.InvariantCultureIgnoreCase) &&
							commitMessage.IndexOf(PackageUpdateCommitMessageBegin, StringComparison.InvariantCultureIgnoreCase) >= 0)
						{
							gitProc.CancelOutputRead();
							if (!gitProc.HasExited)
								gitProc.Kill();
							return;
						}

						// Start collecting files for the new commit
						commitsSinceLastPackage.Add(new GitCommitInfo
						{
							Id = commitId,
							Title = commitTitle,
							Message = commitMessage
						});
						return;
					}
				}

				// File list entry
				GitCommitInfo commitInfo = commitsSinceLastPackage[commitsSinceLastPackage.Count - 1];
				string filePathFull = Path.GetFullPath(Path.Combine(solutionDir, e.Data));
				commitInfo.FilePaths.Add(filePathFull);
				return;
			};
			gitProc.Start();
			gitProc.BeginOutputReadLine();
			gitProc.WaitForExit();

			return commitsSinceLastPackage;
		}
		private static List<ProjectChangeInfo> GetChangesPerProject(ConfigFile config, IEnumerable<ProjectInfo> allProjects, IEnumerable<GitCommitInfo> gitHistory)
		{
			Dictionary<ProjectInfo, ProjectChangeInfo> changesPerProject = new Dictionary<ProjectInfo,ProjectChangeInfo>();
			foreach (GitCommitInfo commit in gitHistory)
			{
				// Iterate over all projects that are affected by this commit
				foreach (ProjectInfo project in allProjects)
				{
					bool isAffectedByCommit = false;
					List<string> localModifiedFiles = new List<string>();
					foreach (string filePath in commit.FilePaths)
					{
						if (IsPathLocatedIn(filePath, project.ProjectRootDir))
						{
							isAffectedByCommit = true;
							localModifiedFiles.Add(filePath);
						}
					}
					if (!isAffectedByCommit) continue;

					// Retrieve the change log information about this project
					ProjectChangeInfo changeInfo;
					if (!changesPerProject.TryGetValue(project, out changeInfo))
					{
						changeInfo = new ProjectChangeInfo
						{
							Project = project,
						};
						changesPerProject.Add(project, changeInfo);
					}

					// Add modified files
					foreach (string filePath in localModifiedFiles)
					{
						changeInfo.ModifiedFilePaths.Add(filePath);
					}

					// Add changelog entries, if we have meaningful comments
					if (!string.IsNullOrEmpty(commit.Title) && !string.IsNullOrEmpty(commit.Message))
					{
						string[] msgItems = commit.Message.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
						changeInfo.Titles.Add(commit.Title);
						changeInfo.ChangeLog.AddRange(msgItems);
					}
				}
			}
			return changesPerProject.Values.ToList();
		}
		private static void RetrieveUpdateModes(ConfigFile config, IEnumerable<ProjectChangeInfo> changes)
		{
			string solutionDir = Path.GetDirectoryName(config.SolutionPath);

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("Collecting Update data...");
			Console.ResetColor();
			Console.WriteLine();
			foreach (ProjectChangeInfo changeInfo in changes)
			{
				string[] nameTokens = changeInfo.Project.NuSpecPackageId.Split('.');

				Console.WriteLine();
				Console.ForegroundColor = ConsoleColor.White;
				Console.WriteLine("{0} {1}", changeInfo.Project.NuSpecPackageId, changeInfo.Project.NuSpecVersion);
				Console.ResetColor();
				Console.WriteLine();

				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("  {0} files changed:", changeInfo.ModifiedFilePaths.Count);
				Console.ResetColor();
				foreach (string changedFile in changeInfo.ModifiedFilePaths)
				{
					string displayedPath = GetRelativePath(changedFile, solutionDir);
					string displayedDir = Path.GetDirectoryName(displayedPath);
					string displayedFileNameWithoutExt = Path.GetFileNameWithoutExtension(displayedPath);
					string displayedExt = Path.GetExtension(displayedPath);

					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.Write("    {0}", displayedDir);
					Console.Write(Path.DirectorySeparatorChar);
					Console.ResetColor();
					Console.Write(displayedFileNameWithoutExt);
					Console.ForegroundColor = ConsoleColor.DarkGreen;
					Console.WriteLine(displayedExt);
					Console.ResetColor();
				}
				Console.WriteLine();

				Console.WriteLine("  {0}", string.Join(", ", changeInfo.Titles));
				foreach (string changeLine in changeInfo.ChangeLog)
				{
					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.WriteLine("    {0}", changeLine);
					Console.ResetColor();
				}
				Console.WriteLine();

				Console.Write("  Update? ");
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write("(m)ajor, m(i)nor, (p)atch, (n)one / (s)kip");
				Console.WriteLine();
				Console.ResetColor();
				Console.Write("  ");
				string userInput = Console.ReadLine();

				// Parse user input
				switch (userInput.ToLower())
				{
					case "major":
					case "m":
						changeInfo.UpdateMode = UpdateMode.Major;
						break;
					case "minor":
					case "i":
						changeInfo.UpdateMode = UpdateMode.Minor;
						break;
					case "patch":
					case "p":
						changeInfo.UpdateMode = UpdateMode.Patch;
						break;
					default:
					case "none":
					case "skip":
					case "n":
					case "s":
						changeInfo.UpdateMode = UpdateMode.None;
						break;
				}
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.WriteLine("  Updating {0} Version", changeInfo.UpdateMode);
				Console.ResetColor();
				Console.WriteLine();
			}
		}
		private static void UpdateVersionNumbers(ConfigFile config, IEnumerable<ProjectChangeInfo> changes)
		{
			foreach (ProjectChangeInfo changeInfo in changes)
			{
				switch (changeInfo.UpdateMode)
				{
					default:
					case UpdateMode.None: continue;
					case UpdateMode.Major:
						changeInfo.Project.NuSpecVersion = new Version(
							changeInfo.Project.NuSpecVersion.Major + 1,
							0,
							0);
						break;
					case UpdateMode.Minor:
						changeInfo.Project.NuSpecVersion = new Version(
							changeInfo.Project.NuSpecVersion.Major,
							changeInfo.Project.NuSpecVersion.Minor + 1,
							0);
						break;
					case UpdateMode.Patch:
						changeInfo.Project.NuSpecVersion = new Version(
							changeInfo.Project.NuSpecVersion.Major,
							changeInfo.Project.NuSpecVersion.Minor,
							changeInfo.Project.NuSpecVersion.Build + 1);
						break;
				}
			}
		}

		private static string GetAbsoluteConfigPath(string relativeBaseDir, string configPath)
		{
			return Path.IsPathRooted(configPath) ? configPath : Path.GetFullPath(Path.Combine(relativeBaseDir, configPath));
		}
		private static bool IsPathLocatedIn(string path, string directory)
		{
			path = Path.GetFullPath(path);
			directory = Path.GetFullPath(directory);
			return path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.InvariantCultureIgnoreCase);
		}
		private static string GetRelativePath(string path, string relativeBaseDir)
		{
			path = Path.GetFullPath(path);
			relativeBaseDir = Path.GetFullPath(relativeBaseDir);
			return path.Substring(relativeBaseDir.Length + 1, path.Length - relativeBaseDir.Length - 1);
		}
		private static int GetPathDepth(string path)
		{
			int depth = 0;
			while (!string.IsNullOrEmpty(path))
			{
				path = Path.GetDirectoryName(path);
				depth++;
			}
			return depth;
		}
	}
}
