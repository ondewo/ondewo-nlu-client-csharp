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
    /// Pins how the GitHub release body is cut out of RELEASE.md. `gh release create -n ""`
    /// succeeds, so a heading whose spelling drifted from what the Makefile greps for, or a section
    /// without its ***** terminator, publishes empty or truncated notes without any error.
    /// </summary>
    public class ReleaseNotesTests
    {
        /// <summary>The product as spelled in the RELEASE.md headings and the Makefile version variable.</summary>
        private const string Product = "NLU";

        private static readonly string VersionVariable = $"ONDEWO_{Product}_VERSION";

        private static readonly Regex Heading = new(
            $@"^## Release ONDEWO {Product} Csharp Client (?<version>\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);

        private static readonly Regex Separator = new(@"^\*{5}", RegexOptions.CultureInvariant);

        private static readonly string RepositoryRoot = FindRepositoryRoot();

        private static string[] ReleaseLines => File.ReadAllLines(Path.Combine(RepositoryRoot, "RELEASE.md"));

        private static string Makefile => File.ReadAllText(Path.Combine(RepositoryRoot, "Makefile"));

        [Fact]
        public void TheMakefileSlicesFromTheHeadingToTheSeparator()
        {
            // A sed range ending at /\*\*/ stops at the first **bold** span of the notes instead.
            string expected =
                $"perl -ne 'print if /Release ONDEWO {Product} Csharp Client ${{{VersionVariable}}}/../^\\*{{5}}/'";

            Assert.Contains("CURRENT_RELEASE_NOTES=`cat RELEASE.md \\", Makefile, StringComparison.Ordinal);
            Assert.Contains(expected, Makefile, StringComparison.Ordinal);
            Assert.Contains("-n \"$(CURRENT_RELEASE_NOTES)\"", Makefile, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryReleaseHeadingIsSpelledAsTheMakefileExpects()
        {
            List<string> headings = ReleaseLines.Where(line => line.Contains("Release ONDEWO", StringComparison.Ordinal)).ToList();

            Assert.NotEmpty(headings);
            Assert.All(headings, line => Assert.Matches(Heading, line));
            List<string> versions = headings.Select(line => Heading.Match(line).Groups["version"].Value).ToList();
            Assert.Equal(versions.Count, versions.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EverySectionEndsAtItsSeparator()
        {
            string[] lines = ReleaseLines;
            List<int> headingIndexes = Enumerable.Range(0, lines.Length).Where(i => Heading.IsMatch(lines[i])).ToList();

            Assert.NotEmpty(headingIndexes);
            for (int n = 0; n < headingIndexes.Count; n++)
            {
                int start = headingIndexes[n];
                int end = n + 1 < headingIndexes.Count ? headingIndexes[n + 1] : lines.Length;
                int separator = Array.FindIndex(lines, start + 1, end - start - 1, line => Separator.IsMatch(line));
                Assert.True(separator > start, $"the section '{lines[start]}' has no ***** separator before the next one");
                Assert.True(
                    lines.Skip(start + 1).Take(separator - start - 1).Any(line => line.Trim().Length > 0),
                    $"the section '{lines[start]}' is empty");
            }
        }

        [Fact]
        public void TheCurrentVersionHasNonEmptyReleaseNotes()
        {
            Match version = Regex.Match(Makefile, $@"^{VersionVariable}=(?<version>\S+)\s*$", RegexOptions.Multiline);
            Assert.True(version.Success, $"no {VersionVariable}= line in the Makefile");

            List<string> slice = Slice(ReleaseLines, $"Release ONDEWO {Product} Csharp Client {version.Groups["version"].Value}");

            Assert.True(slice.Count > 2, $"the release notes of {version.Groups["version"].Value} are empty");
            Assert.Matches(Heading, slice[0]);
            Assert.Matches(Separator, slice[^1]);
            Assert.Single(slice, line => Heading.IsMatch(line));
        }

        /// <summary>What <c>perl -ne 'print if /start/../^\*{5}/'</c> prints: start line to separator.</summary>
        private static List<string> Slice(IEnumerable<string> lines, string startPattern)
        {
            var start = new Regex(startPattern, RegexOptions.CultureInvariant);
            var slice = new List<string>();
            bool inside = false;
            foreach (string line in lines)
            {
                if (!inside && start.IsMatch(line))
                {
                    inside = true;
                    slice.Add(line);
                    continue;
                }

                if (inside)
                {
                    slice.Add(line);
                    if (Separator.IsMatch(line))
                    {
                        inside = false;
                    }
                }
            }

            return slice;
        }

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
