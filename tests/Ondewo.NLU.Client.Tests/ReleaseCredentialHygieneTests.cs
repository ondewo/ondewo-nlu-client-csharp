#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// Release credentials never reach a process argv. <c>/proc/&lt;pid&gt;/cmdline</c> is world-readable,
    /// so a token on the command line of <c>sh</c>, <c>make</c>, <c>docker</c> or <c>dotnet</c> is
    /// visible to every user of the release machine for the life of that process. make expands
    /// <c>$(NAME)</c> and <c>${NAME}</c> BEFORE it runs <c>sh -c '&lt;line&gt;'</c>, so a recipe may only
    /// read a secret as <c>$${NAME}</c>, which the shell expands from the exported environment.
    /// </summary>
    public class ReleaseCredentialHygieneTests
    {
        /// <summary>Every variable name that carries a credential.</summary>
        private const string Secret = @"[A-Z0-9_]*(?:TOKEN|PASSWORD|API_KEY|SECRET|PASSPHRASE)[A-Z0-9_]*";

        private static readonly string RepositoryRoot = FindRepositoryRoot();

        private static string Makefile => File.ReadAllText(Path.Combine(RepositoryRoot, "Makefile"));

        /// <summary>The Makefile's recipe lines (tab-indented), with their 1-based line numbers.</summary>
        private static IEnumerable<(int Line, string Text)> RecipeLines =>
            Makefile.Split('\n').Select((text, index) => (index + 1, text)).Where(line => line.Item2.StartsWith('\t'));

        [Fact]
        public void TheMakefileDeclaresTheReleaseCredentials()
        {
            // Guards the name pattern itself: if it stopped matching, every test below would pass vacuously.
            Assert.Matches(new Regex($"^GITHUB_GH_TOKEN\\?=", RegexOptions.Multiline), Makefile);
            Assert.Matches(new Regex($"^NUGET_API_KEY\\?=", RegexOptions.Multiline), Makefile);
            Assert.Matches(new Regex($"^{Secret}$"), "NUGET_API_KEY");
            Assert.Matches(new Regex($"^{Secret}$"), "GITHUB_GH_TOKEN");
        }

        [Fact]
        public void MakeNeverExpandsASecretIntoARecipeLine()
        {
            var expanded = new Regex($@"(?<!\$)\$[({{]{Secret}[)}}]");
            Assert.Empty(RecipeLines.Where(line => expanded.IsMatch(line.Text)).Select(line => $"Makefile:{line.Line}: {line.Text.Trim()}"));
        }

        [Fact]
        public void TheDevopsReleaseHandsTheCredentialsOverTheEnvironment()
        {
            string recipe = Recipe("run_release_with_devops");

            Assert.DoesNotContain("$(info)", recipe, StringComparison.Ordinal);
            Assert.Contains("set -a", recipe, StringComparison.Ordinal);
            // Anchored: an unanchored grep also picks up comment lines that merely mention a name.
            Assert.Matches(new Regex(@"grep -h -E '\^\("), recipe);
            Assert.Matches(new Regex(@"&& \$\(MAKE\) release\s*$"), recipe);
        }

        [Fact]
        public void NoMakeInvocationGetsACredentialAsAnArgument()
        {
            var assignment = new Regex($@"(?:\$\(MAKE\)|\bmake)\s[^\n]*\b{Secret}=");
            Assert.Empty(RecipeLines.Where(line => assignment.IsMatch(line.Text)).Select(line => $"Makefile:{line.Line}: {line.Text.Trim()}"));
        }

        [Fact]
        public void NoDockerRunGetsACredentialValue()
        {
            // `-e NAME` makes docker copy the value from its own environment; `-e NAME=value` is argv.
            var valued = new Regex($@"(?:-e|--env)[ =]+{Secret}=");
            foreach (string file in ScannedFiles())
            {
                Assert.DoesNotMatch(valued, File.ReadAllText(file));
            }
        }

        [Fact]
        public void NoToolGetsACredentialFlagOnItsArgv()
        {
            var flag = new Regex(
                $@"(?:--api-key|--symbol-api-key|-k|-sk|--token|--password|-p)[ =]+[""']?\$+[({{]?{Secret}|Authorization:[^\n]*{Secret}");
            foreach (string file in ScannedFiles())
            {
                Assert.DoesNotMatch(flag, File.ReadAllText(file));
            }
        }

        [Fact]
        public void DotnetNugetPushReadsTheKeyFromTheEnvironment()
        {
            string recipe = Recipe("push_to_nuget");

            Assert.Contains("dotnet nuget push \"${NUPKG}\"", recipe, StringComparison.Ordinal);
            Assert.DoesNotContain("--api-key", recipe, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\s-k\s"), recipe);
            // An SDK older than 10.0.400 ignores NUGET_API_KEY and would push without any key.
            Assert.Contains("dotnet nuget push --help 2>/dev/null | grep -q NUGET_API_KEY", recipe, StringComparison.Ordinal);
        }

        [Fact]
        public void NoWorkflowInterpolatesASecretIntoARunScript()
        {
            // `${{ secrets.X }}` inside run: is substituted into the script text, i.e. bash's argv and
            // the step log; it belongs in env: and the script reads $X.
            foreach (string workflow in Workflows())
            {
                Assert.Empty(RunScriptLines(File.ReadAllLines(workflow))
                    .Where(line => line.Text.Contains("${{ secrets.", StringComparison.Ordinal))
                    .Select(line => $"{Path.GetFileName(workflow)}:{line.Line}: {line.Text.Trim()}"));
            }
        }

        [Fact]
        public void TheWorkflowScanSeesRunScripts()
        {
            // Guards RunScriptLines: a block scalar and a one-line run: are both recognised, with: is not.
            string[] sample =
            {
                "    steps:",
                "      - name: a",
                "        run: echo ${{ secrets.ONE }}",
                "      - name: b",
                "        with:",
                "          token: ${{ secrets.TWO }}",
                "      - run: |",
                "          set -eu",
                "          curl ${{ secrets.THREE }}",
                "        env:",
                "          X: ${{ secrets.FOUR }}",
            };
            List<string> hits = RunScriptLines(sample).Where(l => l.Text.Contains("secrets.", StringComparison.Ordinal)).Select(l => l.Text.Trim()).ToList();

            Assert.Equal(new[] { "run: echo ${{ secrets.ONE }}", "curl ${{ secrets.THREE }}" }, hits);
        }

        /// <summary>The lines of every <c>run:</c> script (one-line or block scalar), 1-based.</summary>
        private static IEnumerable<(int Line, string Text)> RunScriptLines(string[] lines)
        {
            var run = new Regex(@"^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$");
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = run.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                yield return (i + 1, lines[i]);
                int keyIndent = lines[i].IndexOf("run:", StringComparison.Ordinal);
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string next = lines[j];
                    if (next.Trim().Length > 0 && next.Length - next.TrimStart().Length <= keyIndent)
                    {
                        break;
                    }

                    yield return (j + 1, next);
                }
            }
        }

        /// <summary>The recipe of <paramref name="target"/>: its rule line up to the next blank line.</summary>
        private static string Recipe(string target)
        {
            Match match = Regex.Match(Makefile, $@"^{Regex.Escape(target)}:.*?(?=\n\n|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(match.Success, $"no {target} target in the Makefile");
            return match.Value;
        }

        private static IEnumerable<string> Workflows()
        {
            string directory = Path.Combine(RepositoryRoot, ".github", "workflows");
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.yml").Concat(Directory.EnumerateFiles(directory, "*.yaml"))
                : Enumerable.Empty<string>();
        }

        private static IEnumerable<string> ScannedFiles() =>
            new[] { Path.Combine(RepositoryRoot, "Makefile") }
                .Concat(Directory.EnumerateFiles(RepositoryRoot, "Dockerfile*"))
                .Concat(Workflows());

        private static string FindRepositoryRoot()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "RELEASE.md"))
                    && File.Exists(Path.Combine(directory.FullName, "Makefile")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException("no RELEASE.md + Makefile above " + AppContext.BaseDirectory);
        }
    }
}
