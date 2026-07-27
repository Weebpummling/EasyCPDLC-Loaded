using EasyCPDLC.VNS430;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace EasyCPDLC.Tests
{
    /// <summary>
    /// The GNS430 turns an AOC menu index into a workflow kind by adding it to
    /// <see cref="Vns430WorkflowKind.AocTelex"/>. That is quiet and positional: reorder
    /// the enum block or append a menu item and the unit starts opening the wrong form
    /// with nothing to say so. These pin the arithmetic to the menu it assumes.
    /// </summary>
    public sealed class Vns430AocMenuTests
    {
        private static string[] AocMenuItems()
        {
            FieldInfo field = typeof(Vns430Form).GetField("AocMenuItems",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            return (string[])field.GetValue(null);
        }

        private static Vns430WorkflowKind KindForIndex(int index) =>
            (Vns430WorkflowKind)(index + (int)Vns430WorkflowKind.AocTelex);

        [Fact]
        public void EveryAocMenuIndexOpensTheFormItsLabelPromises()
        {
            string[] items = AocMenuItems();

            (string Label, Vns430WorkflowKind Kind)[] expected =
            {
                ("AOC TELEX", Vns430WorkflowKind.AocTelex),
                ("METAR", Vns430WorkflowKind.AocMetar),
                ("ATIS", Vns430WorkflowKind.AocAtis),
                ("PREDEP CLEARANCE", Vns430WorkflowKind.AocPreDeparture),
                ("OCEANIC CLEARANCE", Vns430WorkflowKind.AocOceanic)
            };

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Label, items[i]);
                Assert.Equal(expected[i].Kind, KindForIndex(i));
            }
        }

        // The last item is handled by index instead of by arithmetic, so it has to stay
        // last or a real workflow kind gets shadowed by the load-control page.
        [Fact]
        public void LoadControlIsTheLastAocMenuItem()
        {
            string[] items = AocMenuItems();

            Assert.Equal("LOAD CONTROL", items[^1]);
        }

        /// <summary>
        /// The company position report is an airline function and is deliberately absent
        /// from this GA unit. It must also stay out of arithmetic range: the index that
        /// would reach it is the one LOAD CONTROL occupies, so appending an AOC item
        /// without thinking would expose it.
        /// </summary>
        [Fact]
        public void CompanyPositionReportIsUnreachableFromTheGnsAocMenu()
        {
            string[] items = AocMenuItems();

            Assert.DoesNotContain(Vns430WorkflowKind.AocCompanyPosition,
                Enumerable.Range(0, items.Length - 1).Select(KindForIndex));

            // And nothing on the menu advertises it.
            Assert.DoesNotContain(items, label => label.Contains("POS", StringComparison.OrdinalIgnoreCase));
        }

        // Same positional trap on the ATC side, which the enum already carries a warning
        // about - the block must stay contiguous and in menu order.
        [Fact]
        public void EveryAtcMenuIndexOpensTheFormItsLabelPromises()
        {
            FieldInfo field = typeof(Vns430Form).GetField("AtcMenuItems",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            string[] items = (string[])field.GetValue(null);

            Vns430WorkflowKind[] expected =
            {
                Vns430WorkflowKind.AtcDirect,
                Vns430WorkflowKind.AtcLevel,
                Vns430WorkflowKind.AtcSpeed,
                Vns430WorkflowKind.AtcWhenCanWe,
                Vns430WorkflowKind.AtcFreeText,
                Vns430WorkflowKind.AtcPositionReport
            };

            Assert.Equal(expected.Length, items.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], (Vns430WorkflowKind)(i + (int)Vns430WorkflowKind.AtcDirect));
            }
        }
    }
}
