using EasyCPDLC.VNS430;
using System;
using System.Linq;
using Xunit;

namespace EasyCPDLC.Tests
{
    /// <summary>
    /// The GNS430 request menus. Rows now name their own workflow kind, so the old
    /// positional arithmetic cannot mis-pair a label with a form. These pin what the
    /// menus offer, and that every row the form can select is one the renderer can draw.
    /// </summary>
    public sealed class Vns430AocMenuTests
    {
        [Fact]
        public void EveryAocRowOpensTheFormItsLabelPromises()
        {
            (string Label, Vns430WorkflowKind? Kind)[] expected =
            {
                ("AOC TELEX", Vns430WorkflowKind.AocTelex),
                ("METAR", Vns430WorkflowKind.AocMetar),
                ("ATIS", Vns430WorkflowKind.AocAtis),
                ("PREDEP CLEARANCE", Vns430WorkflowKind.AocPreDeparture),
                ("OCEANIC CLEARANCE", Vns430WorkflowKind.AocOceanic),
                ("LOAD CONTROL", null)     // a page, not a workflow
            };

            Assert.Equal(expected.Length, Vns430RequestMenus.Aoc.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Label, Vns430RequestMenus.Aoc[i].Label);
                Assert.Equal(expected[i].Kind, Vns430RequestMenus.Aoc[i].Kind);
            }
        }

        [Fact]
        public void EveryAtcRowOpensTheFormItsLabelPromises()
        {
            (string Label, Vns430WorkflowKind Kind)[] expected =
            {
                ("DIRECT TO", Vns430WorkflowKind.AtcDirect),
                ("LEVEL", Vns430WorkflowKind.AtcLevel),
                ("SPEED", Vns430WorkflowKind.AtcSpeed),
                ("WHEN CAN WE", Vns430WorkflowKind.AtcWhenCanWe),
                ("FREE TEXT", Vns430WorkflowKind.AtcFreeText),
                ("POSITION REP", Vns430WorkflowKind.AtcPositionReport)
            };

            Assert.Equal(expected.Length, Vns430RequestMenus.Atc.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Label, Vns430RequestMenus.Atc[i].Label);
                Assert.Equal(expected[i].Kind, Vns430RequestMenus.Atc[i].Kind);
            }
        }

        // Exactly one row may be a page rather than a request, or the null case in
        // ActivateMenuItem quietly starts standing for more than load control.
        [Fact]
        public void LoadControlIsTheOnlyRowWithoutAWorkflow()
        {
            Vns430MenuRow[] pageRows = Vns430RequestMenus.Aoc.Where(row => row.Kind == null).ToArray();

            Vns430MenuRow only = Assert.Single(pageRows);
            Assert.Equal("LOAD CONTROL", only.Label);
            Assert.DoesNotContain(Vns430RequestMenus.Atc, row => row.Kind == null);
        }

        /// <summary>
        /// The company position report is an airline function and is deliberately absent
        /// from this GA unit.
        /// </summary>
        [Fact]
        public void CompanyPositionReportIsAbsentFromTheGnsAocMenu()
        {
            Assert.DoesNotContain(Vns430WorkflowKind.AocCompanyPosition,
                Vns430RequestMenus.Aoc.Select(row => row.Kind));

            Assert.DoesNotContain(Vns430RequestMenus.Aoc,
                row => row.Label.Contains("POS", StringComparison.OrdinalIgnoreCase));
        }

        // The bug the shared table exists to prevent: the renderer drew five ATC rows
        // while the form let the selection reach a sixth, so POSITION REP was selectable
        // and invisible. Every selectable row must carry a label the renderer can draw.
        [Fact]
        public void EveryRowHasALabelToDraw()
        {
            Assert.Equal(Vns430RequestMenus.Atc.Length, Vns430RequestMenus.Labels(Vns430RequestMenus.Atc).Length);
            Assert.Equal(Vns430RequestMenus.Aoc.Length, Vns430RequestMenus.Labels(Vns430RequestMenus.Aoc).Length);

            Assert.All(Vns430RequestMenus.Atc, row => Assert.False(string.IsNullOrWhiteSpace(row.Label)));
            Assert.All(Vns430RequestMenus.Aoc, row => Assert.False(string.IsNullOrWhiteSpace(row.Label)));
        }
    }
}
