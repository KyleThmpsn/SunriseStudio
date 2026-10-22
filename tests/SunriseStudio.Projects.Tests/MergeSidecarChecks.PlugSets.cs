using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static partial class MergeSidecarChecks
{
    /** Independent mods extend one stock choice list and each create a private randomized pool. */
    private static void PlugSetBuild(string parent)
    {
        string directory = Path.Combine(parent, "plug-merge-install", "packages");
        Directory.CreateDirectory(directory);
        byte[] stock = PlugSetTable(), items = IndexTable(StockRows, IndexRowClass),
            displays = IndexTable(StockRows, DisplayRowClass);
        Install(directory, PlugSets, PlugSetClass, stock);
        Install(directory, ItemIndex, IndexClass, items);
        Install(directory, DisplayIndex, DisplayIndexClass, displays);
        InstallSocketManifest(directory);
        using var catalog = PackageCatalog.Open(Path.GetDirectoryName(directory)!, cacheOptions: new() { Enabled = false });
        for (int mod = 0; mod < 2; mod++)
        {
            var project = StudioProject.Create(Path.Combine(parent, $"plug-merge-{mod}"), $"Plug merge {mod}");
            ushort package = (ushort)(0x0C00 + mod);
            uint tag = 0x80800000U + ((uint)package << 13), hash = 0xA1000000U + (uint)mod;
            var (item, initial, _) = ItemDefinition(StockRows);
            int socket = initial - 2;
            BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(socket + 32), 1);
            // Set 1 is rolled from; only the reusable set 0 should gain a shared choice.
            var shared = InvestmentPlugSets.Rebuild(stock, stock, StockRows, 1, true, new HashSet<int> { 1 }).Table;
            var (definition, grown) = InvestmentPlugSets.FollowPools(item, stock, shared, [0]);
            Check(grown is not null, "Adding a plug did not create its private randomized pool.");
            // Both socket references use the same new pool to exercise both remapping sites.
            BinaryPrimitives.WriteUInt16LittleEndian(definition.AsSpan(socket + 12), 2);
            project.ApplyEdit(PlugSets, grown!, catalog);
            project.ApplyEdit(ItemIndex, AppendRow(items, hash, tag), catalog);
            project.ApplyEdit(DisplayIndex, AppendRow(displays, hash, tag + 1), catalog);
            project.ApplyEdit(InvestmentItemArt.TableTag,
                InvestmentItemArt.CloneRow(catalog.ReadOriginal(InvestmentItemArt.TableTag), 0, StockRows), catalog);
            project.AddAsset("item.bin", definition, new(package, ItemClass, 0x100A, $"test:plug-{mod}", 0));
            byte[] display = new byte[160];
            BinaryPrimitives.WriteInt64LittleEndian(display, display.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(display.AsSpan(128), ushort.MaxValue);
            project.AddAsset("display.bin", display, new(package, TableMergeRecipes.ItemDisplayClass, 0x100A, $"test:display-{mod}", 1));
            var build = project.Build(catalog);
            string packages = Path.Combine(build.OutputDirectory, "packages");
            var sets = Bound(Path.Combine(packages, $"w64_test_{(PlugSets - 0x80800000U) >> 13:x4}_1.pkg"));
            var entry = sets.Entries.Single(value => value.Index == (PlugSets & 0x1FFF));
            Check(entry.Kind == SidecarEntryKind.Table && entry.Ops.Count == 2, "Plug sets were emitted as an opaque edit.");
            var top = entry.Ops[0]; var nested = entry.Ops[1];
            Check(top.StockCount == 2 && top.ModCount == 3 && top.Limit == ushort.MaxValue
                && nested.ParentOp == 0 && nested.ParentRow == 0 && nested.Descriptor == 8
                && nested.StockCount == 1 && nested.ModCount == 2,
                "Plug sets did not describe both new sets and choices added to a stock set.");
            var setShape = sets.Shapes[(int)top.Shape];
            var memberShape = sets.Shapes[setShape.Fields.Single().Ref];
            Check(setShape.Stride == 24 && memberShape.Stride == 32
                && memberShape.Fields.Single().RowOffset == 8
                && memberShape.Fields.Single().Kind == MergeFieldKind.OwnedArray,
                "The plug-set shape omitted member condition arrays.");
            Check(entry.Sites.Count == 2 && entry.Sites.All(site => site.Width == 4
                && sets.Targets[(int)site.Target].Table == ItemIndex),
                "Shared and private choices did not both remap their added item indices.");
            var added = Bound(Directory.GetFiles(packages, $"*_{package:x4}_0.pkg").Single());
            var addedItem = added.Entries.Single(value => value.Index == 0);
            Check(addedItem.Sites.Where(site => added.Targets[(int)site.Target].Table == PlugSets)
                .Select(site => (site.Offset, site.Width)).SequenceEqual(new[] { ((uint)(socket + 12), (byte)2), ((uint)(socket + 32), (byte)2) }),
                "Socket reusable and randomized set indices are not both remapped.");
        }
        Check(catalog.ReadOriginal(PlugSets).SequenceEqual(stock), "Building changed the installed plug sets.");
        Console.WriteLine("plug_set_merge two_mods=pass stock_members=nested new_sets=append conditions=owned socket_sites=both");
    }

    private static byte[] PlugSetTable()
    {
        byte[] table = new byte[320];
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(at), value);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), value);
        void Array(int at, int header, int count, uint type)
        {
            Q(at, count); Q(at + 8, header - at - 8);
            U(header - 4, ArrayMarker); Q(header, count); U(header + 8, type);
        }
        Q(0, table.Length); Array(8, 32, 2, PlugSetRowClass);
        foreach (var (row, members, condition, argument) in new[] { (48, 112, 240, 77u), (72, 176, 288, 88u) })
        {
            U(row, (uint)row); Array(row + 8, members, 1, PlugClass);
            U(members + 16, 1); U(members + 40, 0x3F800000);
            Array(members + 24, condition, 1, 0x80807D31);
            table[condition + 16] = 11; U(condition + 20, argument);
        }
        return table;
    }

    private static void InstallSocketManifest(string directory)
    {
        const uint tag = 0x81A2926F, root = tag & ~0x1FFFu;
        const ushort package = (ushort)((tag - 0x80800000U) >> 13);
        const int entry = (int)(tag & 0x1FFF);
        byte[] bytes = new byte[176];
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        Q(0, bytes.Length); U(8, tag); U(12, root);
        Q(16, 1); Q(24, 64 - 24); U(60, ArrayMarker); Q(64, 1); U(72, 0x80809EFB);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(80), package);
        Q(104, 2); Q(112, 144 - 112); U(140, ArrayMarker); Q(144, 2); U(152, 0x8080000A);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(162), entry);
        var entries = Enumerable.Range(0, entry + 1).Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
        entries[0] = new(0x80800001, 0x200A, new byte[16]);
        entries[InvestmentItemArt.TableTag & 0x1FFF] = new(InvestmentItemArt.ClassId, 0x100A, ItemArtTable());
        entries[entry] = new(PackageManifest.ClassId, 0x100A, bytes);
        PackageWriter.WriteNew(Path.Combine(directory, $"w64_test_{package:x4}_0.pkg"), package, entries);
    }

    private static byte[] ItemArtTable()
    {
        byte[] table = new byte[272];
        BinaryPrimitives.WriteInt64LittleEndian(table, table.Length);
        foreach (var (field, header, count, type) in new[]
        {
            (8, 96, 1, 0x80800014u), (24, 128, 1, 0x80800007u),
            (40, 160, 1, 0x808056D6u), (56, 192, StockRows, 0x808056D1u)
        })
        {
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(field), count);
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(field + 8), header - field - 8);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(header - 4), ArrayMarker);
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(header), count);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(header + 8), type);
        }
        return table;
    }
}
