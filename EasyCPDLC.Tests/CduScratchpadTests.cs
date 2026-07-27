using EasyCPDLC;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace EasyCPDLC.Tests
{
    /// <summary>
    /// The CDU keypad only writes to the scratchpad on pages that opt in. A page whose
    /// line key reads the scratchpad but is not on that list has a silently dead keypad:
    /// characters go nowhere and the line key then consumes an empty buffer. That is
    /// exactly how the AOC company address shipped broken, so it is pinned here.
    /// </summary>
    public sealed class CduScratchpadTests
    {
        private static readonly MainForm.CduPageId[] TypingPages =
        {
            MainForm.CduPageId.Request,
            MainForm.CduPageId.Logon,
            MainForm.CduPageId.Aoc,
            MainForm.CduPageId.SetupAccount,
            MainForm.CduPageId.SetupPrinter
        };

        [Fact]
        public void EveryPageWithATypedFieldAcceptsTheScratchpad()
        {
            Assert.All(TypingPages, page =>
                Assert.True(MainForm.CduPageAcceptsScratchpad(page), page + " does not accept typing"));
        }

        // The AOC page is the one that regressed: the company address is typed into the
        // scratchpad and read by a line key, so it must accept typing.
        [Fact]
        public void AocPageAcceptsTypingForTheCompanyAddress()
        {
            Assert.True(MainForm.CduPageAcceptsScratchpad(MainForm.CduPageId.Aoc));
        }

        [Theory]
        [InlineData(nameof(MainForm.CduPageId.Menu))]
        [InlineData(nameof(MainForm.CduPageId.Messages))]
        [InlineData(nameof(MainForm.CduPageId.MessageDetail))]
        [InlineData(nameof(MainForm.CduPageId.Setup))]
        [InlineData(nameof(MainForm.CduPageId.SetupTechnical))]
        public void PagesWithNoTypedFieldDoNotAcceptTheScratchpad(string pageName)
        {
            MainForm.CduPageId page = Enum.Parse<MainForm.CduPageId>(pageName);

            Assert.False(MainForm.CduPageAcceptsScratchpad(page));
        }

        // Source-level guard for the other half of the bug: a page can accept typing and
        // still look dead if it never draws the scratchpad line. Every render method that
        // belongs to a typing page has to call RenderCduScratchpad.
        [Fact]
        public void EveryTypingPageDrawsTheScratchpadLine()
        {
            string source = ReadCduSource();

            // Render method -> the page it draws, for the pages that accept typing.
            (string Method, string Page)[] renderers =
            {
                ("RenderCduRequest", "Request"),
                ("RenderCduLogon", "Logon"),
                ("RenderCduAocCompany", "Aoc"),
                ("RenderCduSetupAccount", "SetupAccount"),
                ("RenderCduSetupPrinter", "SetupPrinter")
            };

            foreach ((string method, string page) in renderers)
            {
                string body = ExtractMethodBody(source, method);
                Assert.False(string.IsNullOrEmpty(body), method + " was not found");
                Assert.Contains("RenderCduScratchpad", body);
            }
        }

        private static string ReadCduSource()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null &&
                   !File.Exists(Path.Combine(directory.FullName, "EasyCPDLC", "VNS430", "Cdu", "MainForm.Cdu.cs")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return File.ReadAllText(Path.Combine(directory.FullName, "EasyCPDLC", "VNS430", "Cdu", "MainForm.Cdu.cs"));
        }

        /// <summary>Brace-matched body of a method, so nested blocks are included.</summary>
        private static string ExtractMethodBody(string source, string methodName)
        {
            Match signature = Regex.Match(source, @"\b" + Regex.Escape(methodName) + @"\s*\([^)]*\)\s*\r?\n?\s*\{");
            if (!signature.Success)
            {
                return string.Empty;
            }

            int index = source.IndexOf('{', signature.Index);
            int depth = 0;
            for (int i = index; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(index, i - index + 1);
                    }
                }
            }

            return string.Empty;
        }
    }
}
