using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Gif320Sharp_Test
{
	[TestClass]
	public sealed class Gif320CliCompatibilityTests
	{
		[TestMethod]
		public void CliMatchesOriginalGif320ForJimmGifPipeModeDefaults()
		{
			string repoRoot = FindRepositoryRoot();
			string original = FindOriginalGif320Executable(repoRoot);
			if (string.IsNullOrEmpty(original))
			{
				Assert.Inconclusive(
					"Could not find or build the original C gif320 executable."
				);
			}

			string imagePath = Path.Combine(
				repoRoot,
				"Gif320Sharp",
				"ExampleImages",
				"jimm.gif"
			);
			Assert.IsTrue(File.Exists(imagePath), "Expected ExampleImages/jimm.gif.");
			byte[] image = File.ReadAllBytes(imagePath);

			byte[] originalOutput = RunProcess(
				original,
				"-p",
				image,
				Path.Combine(repoRoot, "gif320")
			);
			ProcessInvocation managedCli = GetManagedCliInvocation(
				repoRoot,
				"-p --no-auto --threshold 50 25 --balance 30 40 10 --ratio 0.8"
			);
			byte[] managedOutput = RunProcess(
				managedCli.Executable,
				managedCli.Arguments,
				image,
				repoRoot
			);

			CollectionAssert.AreEqual(
				originalOutput,
				managedOutput,
				"Gif320Sharp pipe-mode output must match original gif320 for ExampleImages/jimm.gif with original default parameters."
			);
		}

		[TestMethod]
		public void CliInteractiveModeCanTogglePreviewModes()
		{
			string repoRoot = FindRepositoryRoot();
			string imagePath = Path.Combine(
				repoRoot,
				"Gif320Sharp",
				"ExampleImages",
				"jimm.gif"
			);
			Assert.IsTrue(File.Exists(imagePath), "Expected ExampleImages/jimm.gif.");

			ProcessInvocation managedCli = GetManagedCliInvocation(
				repoRoot,
				"--interactive-compat " + Quote(imagePath)
			);
			byte[] outputBytes = RunProcess(
				managedCli.Executable,
				managedCli.Arguments,
				Encoding.ASCII.GetBytes("mode 80x24\nmode old\nq\n"),
				repoRoot
			);
			string output = Encoding.ASCII.GetString(outputBytes);

			StringAssert.Contains(output, "GIF320> ");
			StringAssert.Contains(output, "GIF320 80x24> ");
			StringAssert.Contains(output, "\u001b[2;32f");
			StringAssert.Contains(output, "\u001b[1;1f");
		}

		[TestMethod]
		public void CliInteractiveModeCanRunAdvancedTuneCommand()
		{
			string repoRoot = FindRepositoryRoot();
			string imagePath = Path.Combine(
				repoRoot,
				"Gif320Sharp",
				"ExampleImages",
				"jimm.gif"
			);
			Assert.IsTrue(File.Exists(imagePath), "Expected ExampleImages/jimm.gif.");

			ProcessInvocation managedCli = GetManagedCliInvocation(
				repoRoot,
				"--interactive-compat " + Quote(imagePath)
			);
			byte[] outputBytes = RunProcess(
				managedCli.Executable,
				managedCli.Arguments,
				Encoding.ASCII.GetBytes("advanced\nq\n"),
				repoRoot
			);
			string output = Encoding.ASCII.GetString(outputBytes);

			StringAssert.Contains(output, "Tone: advanced");
		}

		private static ProcessInvocation GetManagedCliInvocation(
			string repoRoot,
			string arguments
		)
		{
			string configuration = GetBuildConfiguration();
			string outputDirectory = Path.Combine(
				repoRoot,
				"Gif320Sharp",
				"Gif320Sharp",
				"bin",
				configuration,
				"net10.0"
			);
			string cliExe = Path.Combine(
				outputDirectory,
				OperatingSystem.IsWindows() ? "gif320sharp.exe" : "gif320sharp"
			);
			if (File.Exists(cliExe))
			{
				return new ProcessInvocation(cliExe, arguments);
			}

			string cliDll = Path.Combine(outputDirectory, "gif320sharp.dll");
			Assert.IsTrue(File.Exists(cliDll), "Expected Gif320Sharp CLI to be built.");
			return new ProcessInvocation("dotnet", Quote(cliDll) + " " + arguments);
		}

		private static string GetBuildConfiguration()
		{
			DirectoryInfo directory = new(AppContext.BaseDirectory);
			DirectoryInfo? configuration = directory.Parent;
			return configuration?.Name ?? "Debug";
		}

		private static byte[] RunProcess(
			string executable,
			string arguments,
			byte[] stdin,
			string workingDirectory
		)
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = executable,
				Arguments = arguments,
				WorkingDirectory = workingDirectory,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			using Process process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Failed to start " + executable);
			using (Stream input = process.StandardInput.BaseStream)
			{
				input.Write(stdin, 0, stdin.Length);
			}

			using var output = new MemoryStream();
			process.StandardOutput.BaseStream.CopyTo(output);
			string stderr = process.StandardError.ReadToEnd();
			if (!process.WaitForExit(30000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Fail(executable + " timed out.");
			}

			Assert.AreEqual(
				0,
				process.ExitCode,
				executable + " failed: " + stderr
			);
			return output.ToArray();
		}

		private static string FindRepositoryRoot()
		{
			DirectoryInfo? directory = new(AppContext.BaseDirectory);
			while (directory != null)
			{
				if (Directory.Exists(Path.Combine(directory.FullName, "Gif320Sharp"))
					&& Directory.Exists(Path.Combine(directory.FullName, "gif320")))
				{
					return directory.FullName;
				}

				directory = directory.Parent;
			}

			Assert.Fail("Could not locate repository root.");
			throw new InvalidOperationException();
		}

		private static string FindOriginalGif320Executable(string repoRoot)
		{
			string gif320Root = Path.Combine(repoRoot, "gif320");
			string[] candidates =
			[
				Path.Combine(gif320Root, "gif320"),
				Path.Combine(gif320Root, "gif320.exe"),
				Path.Combine(gif320Root, "bin", "x64", "Debug", "gif320.exe"),
				Path.Combine(gif320Root, "bin", "x64", "Release", "gif320.exe"),
				Path.Combine(gif320Root, "bin", "Win32", "Debug", "gif320.exe"),
				Path.Combine(gif320Root, "bin", "Win32", "Release", "gif320.exe"),
			];

			string existing = FindExistingExecutable(candidates);
			if (!string.IsNullOrEmpty(existing))
			{
				return existing;
			}

			if (IsUnixLike())
			{
				ProcessOutput? gccBuild = TryBuildOriginalWithGcc(gif320Root);
				if (gccBuild == null)
				{
					Assert.Inconclusive(
						"Could not find gif320/gif320 and gcc is not available to build it."
					);
				}

				if (gccBuild.ExitCode != 0)
				{
					Assert.Inconclusive(
						"Could not build gif320 with gcc: "
						+ TrimProcessOutput(gccBuild)
					);
				}

				existing = FindExistingExecutable(candidates);
				if (!string.IsNullOrEmpty(existing))
				{
					return existing;
				}

				Assert.Inconclusive(
					"gcc built gif320 successfully, but gif320/gif320 was not found."
				);
			}

			string project = Path.Combine(gif320Root, "gif320.vcxproj");
			if (!File.Exists(project))
			{
				return string.Empty;
			}

			string msbuild = FindMsBuildExecutable(repoRoot);
			if (string.IsNullOrEmpty(msbuild))
			{
				Assert.Inconclusive(
					"gif320/gif320.vcxproj exists, but MSBuild could not be found."
				);
			}

			ProcessOutput build = RunTextProcess(
				msbuild,
				Quote(project) + " /p:Configuration=Debug /p:Platform=x64 /m",
				repoRoot
			);
			if (build.ExitCode != 0)
			{
				Assert.Inconclusive(
					"Could not build gif320/gif320.vcxproj: "
					+ TrimProcessOutput(build)
				);
			}

			existing = FindExistingExecutable(candidates);
			if (!string.IsNullOrEmpty(existing))
			{
				return existing;
			}

			Assert.Inconclusive(
				"gif320/gif320.vcxproj built successfully, but gif320.exe was not found under gif320/bin."
			);
			return string.Empty;
		}

		private static string FindExistingExecutable(string[] candidates)
		{
			foreach (string candidate in candidates)
			{
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}

			return string.Empty;
		}

		private static bool IsUnixLike()
		{
			return !OperatingSystem.IsWindows();
		}

		private static ProcessOutput? TryBuildOriginalWithGcc(string gif320Root)
		{
			try
			{
				return RunTextProcess(
					"gcc",
					"-std=c99 -Wall -g -o gif320 develop.c primary.c misc.c readgif.c vtgraph.c",
					gif320Root
				);
			}
			catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
			{
				return null;
			}
		}

		private static string FindMsBuildExecutable(string repoRoot)
		{
			string? fromEnvironment = Environment.GetEnvironmentVariable("MSBUILD_EXE");
			if (!string.IsNullOrWhiteSpace(fromEnvironment)
				&& File.Exists(fromEnvironment))
			{
				return fromEnvironment;
			}

			string programFilesX86 = Environment.GetFolderPath(
				Environment.SpecialFolder.ProgramFilesX86
			);
			string vswhere = Path.Combine(
				programFilesX86,
				"Microsoft Visual Studio",
				"Installer",
				"vswhere.exe"
			);
			if (File.Exists(vswhere))
			{
				ProcessOutput output = RunTextProcess(
					vswhere,
					"-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
					repoRoot
				);
				if (output.ExitCode == 0)
				{
					string[] lines = output.StandardOutput.Split(
						new[] { '\r', '\n' },
						StringSplitOptions.RemoveEmptyEntries
					);
					foreach (string line in lines)
					{
						string candidate = line.Trim();
						if (File.Exists(candidate))
						{
							return candidate;
						}
					}
				}
			}

			string programFiles = Environment.GetFolderPath(
				Environment.SpecialFolder.ProgramFiles
			);
			string[] commonPaths =
			[
				Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"),
				Path.Combine(programFiles, "Microsoft Visual Studio", "17", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"),
				Path.Combine(programFilesX86, "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"),
				Path.Combine(programFilesX86, "Microsoft Visual Studio", "2019", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"),
			];
			foreach (string candidate in commonPaths)
			{
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}

			return string.Empty;
		}

		private static ProcessOutput RunTextProcess(
			string executable,
			string arguments,
			string workingDirectory
		)
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = executable,
				Arguments = arguments,
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			using Process process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Failed to start " + executable);
			string stdout = process.StandardOutput.ReadToEnd();
			string stderr = process.StandardError.ReadToEnd();
			if (!process.WaitForExit(30000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Inconclusive(executable + " timed out.");
			}

			return new ProcessOutput(process.ExitCode, stdout, stderr);
		}

		private static string TrimProcessOutput(ProcessOutput output)
		{
			string combined = (output.StandardError + Environment.NewLine + output.StandardOutput).Trim();
			if (combined.Length <= 2000)
			{
				return combined;
			}

			return combined.Substring(0, 2000) + "...";
		}

		private static string Quote(string value)
		{
			return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
		}

		private sealed class ProcessOutput
		{
			public ProcessOutput(
				int exitCode,
				string standardOutput,
				string standardError
			)
			{
				ExitCode = exitCode;
				StandardOutput = standardOutput;
				StandardError = standardError;
			}

			public int ExitCode { get; }

			public string StandardOutput { get; }

			public string StandardError { get; }
		}

		private sealed class ProcessInvocation
		{
			public ProcessInvocation(string executable, string arguments)
			{
				Executable = executable;
				Arguments = arguments;
			}

			public string Executable { get; }

			public string Arguments { get; }
		}
	}
}
