using System.Buffers.Binary;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class InvestmentSocketChecks
{
    /** Shared member lists must separate without moving their condition streams. */
    public static void Run(string parent)
    {
        const uint first = 0x80A00000;
        string install = Path.Combine(parent, "socket-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] original = Weapon(conditions: true), plug = new byte[200], display = new byte[160];
        Q(plug, 0, plug.Length); plug[184] = 15;
        Q(display, 0, display.Length); S(display, InvestmentItemClone.DisplayIcon, ushort.MaxValue);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_socket_fixture_0100_0.pkg"), 0x100,
        [
            new(0x80807BE5, 8 << 9, Index(first + 2)),
            new(0x80805CDA, 8 << 9, Index(first + 6)),
            new(InvestmentCatalog.ItemClass, 8 << 9, original),
            new(InvestmentCatalog.ItemClass, 8 << 9, plug),
            new(InvestmentCatalog.ItemClass, 8 << 9, plug),
            new(InvestmentCatalog.ItemClass, 8 << 9, plug),
            new(InvestmentCatalog.DisplayClass, 8 << 9, display),
            new(InvestmentCatalog.DisplayClass, 8 << 9, display),
            new(InvestmentCatalog.DisplayClass, 8 << 9, display),
            new(InvestmentCatalog.DisplayClass, 8 << 9, display),
            new(InvestmentItemArt.ClassId, 8 << 9, InvestmentItemArtChecks.Fixture(4)),
            new(0x80802DFB, 8 << 9, ReusableSet())
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var sources = new InvestmentSources(Items: first, Displays: first + 1, StringBanks: uint.MaxValue,
            ItemArt: first + 10, PlugSets: first + 11);
        var catalog = InvestmentCatalog.Load(packages, sources: sources);
        uint tag = first + 2;
        ChoiceMoves(catalog, tag);
        var socket = catalog.ReadItem(tag, original).Sockets[0];
        Check(catalog.SocketPlugTemplates(tag, original, socket).SequenceEqual(new ushort[] { 1, 3 }),
            "Embedded plug templates did not preserve item indices.");
        Reject(() => catalog.SetSocketInitialPlug(tag, original, socket, 2));
        byte[] added = catalog.AddSocketPlug(tag, original, socket, 2, 1);
        VerifyCopy(catalog, tag, original, added, conditionTarget: 576);
        byte[] unconditional = catalog.AddSocketPlug(tag, original, socket, 2, 1, preserveTemplateCondition: false);
        int unconditionalData = Target(unconditional, 328) + 16;
        Check(Q(unconditional, unconditionalData + 72) == 0 && Q(unconditional, unconditionalData + 80) == 0
            && Target(unconditional, unconditionalData + 16) == 576,
            "The authored unconditional choice changed another member's condition.");
        Untouched(original, unconditional, 320);
        Reject(() => catalog.AddSocketPlug(tag, original, socket, 1, 1, preserveTemplateCondition: false));
        Check(catalog.AddSocketPlug(tag, added, catalog.ReadItem(tag, added).Sockets[0], 2, 1)
            .SequenceEqual(added), "Adding the same plug twice grew the pool.");
        var addedSocket = catalog.ReadItem(tag, added).Sockets[0];
        byte[] selected = catalog.SetSocketInitialPlug(tag, added, addedSocket, 2);
        Check(catalog.ReadItem(tag, selected).Sockets[0].InitialPlug == 2
            && catalog.ReadItem(tag, selected).Sockets[1].InitialPlug == 1,
            "Changing the initial plug changed the other socket.");
        byte[] isolated = catalog.KeepSocketPlug(tag, selected, catalog.ReadItem(tag, selected).Sockets[0], 2);
        var isolatedDetail = catalog.ReadItem(tag, isolated);
        Check(isolatedDetail.Sockets[0].AllowedPlugs.SequenceEqual(new ushort[] { 2 })
            && isolatedDetail.Sockets[1].AllowedPlugs.SequenceEqual(new ushort[] { 1, 3 }),
            "Keeping an authored plug retained a donor choice or changed a shared socket.");
        int isolatedMember = Target(isolated, 328) + 16;
        Check(Target(isolated, isolatedMember + 16) == 576
            && U(isolated, isolatedMember + 4) == 0xDEADBEEF
            && U(isolated, isolatedMember + 24) == 0x3F800000,
            "Keeping an authored plug changed its condition or unknown fields.");
        Check(catalog.KeepSocketPlug(tag, isolated, isolatedDetail.Sockets[0], 2).SequenceEqual(isolated),
            "Keeping the same isolated plug grew the item again.");
        Reject(() => catalog.KeepSocketPlug(tag, isolated, isolatedDetail.Sockets[0], 1));
        Reject(() => catalog.KeepSocketPlug(tag, isolated, addedSocket, 2));
        Reject(() => catalog.SetSocketInitialPlug(tag, selected, addedSocket, 1));
        Reject(() => catalog.AddSocketPlug(tag, added, socket, 2, 1));
        Reject(() => catalog.AddSocketPlug(tag, original, socket, ushort.MaxValue, 1));
        Reject(() => catalog.AddSocketPlug(tag, original, socket, 0x8000, 1));
        Reject(() => catalog.AddSocketPlug(tag, original, socket, 2, 2));
        Reject(() => catalog.AddSocketPlug(tag, original, socket with { Type = 4 }, 2, 1));

        foreach (var change in new Action<byte[]>[]
        {
            bytes => Q(bytes, 0, bytes.Length - 1),
            bytes => U(bytes, 196, 0x808077B7),
            bytes => U(bytes, 456, 0x808077C6),
            bytes => Q(bytes, 480, long.MaxValue),
            bytes => U(bytes, 584, 0x80803033),
            bytes => U(bytes, 464, 0x8000),
            bytes => U(bytes, 496, 1),
            bytes => { Q(bytes, 320, 0); Q(bytes, 328, 0); },
            bytes => { Q(bytes, 200, 13); Q(bytes, 240, 13); }
        })
        {
            byte[] malformed = (byte[])original.Clone(); change(malformed);
            Reject(() => catalog.AddSocketPlug(tag, malformed, socket, 2, 1));
        }
        byte[] tail = Weapon(conditions: false);
        byte[] tailAdded = catalog.AddSocketPlug(tag, tail, catalog.ReadItem(tag, tail).Sockets[0], 2, 1);
        VerifyCopy(catalog, tag, tail, tailAdded, conditionTarget: -1);

        var project = StudioProject.Create(Path.Combine(parent, "socket-project"), "Socket choices");
        project.ApplyEdit(tag, selected, packages);
        var reopened = StudioProject.Open(project.ManifestPath);
        Check(reopened.ReadAsset(tag, packages).SequenceEqual(selected)
            && packages.ReadOriginal(tag).SequenceEqual(original), "Socket edits did not reopen without changing the base.");
        var itemPlan = ItemAuthoring.PlanClone(packages, project.Name, catalog.FindItem(tag)!, selected,
            display, 0x101, 0, "Socket prototype", null, sources);
        Check(itemPlan.ItemIndex == 4 && itemPlan.DefinitionTag == 0x80A02000
            && InvestmentItemClone.ReadIndexRows(itemPlan.Edits[first]).Last().Hash == itemPlan.DefinitionHash
            && InvestmentItemArt.Count(itemPlan.Edits[first + 10]) == 5,
            "The planned item lost its new identity.");
        var created = project.ApplyTransaction(itemPlan.Additions, _ => new(itemPlan.Edits), packages);
        reopened = StudioProject.Open(project.ManifestPath);
        Check(reopened.ReadAddedAsset(created[0].Id, packages).SequenceEqual(
                InvestmentItemClone.SetDefinitionHash(selected, itemPlan.DefinitionHash))
            && packages.ReadOriginal(tag).SequenceEqual(original), "The planned clone changed its installed base.");
        Reject(() => ItemAuthoring.PlanClone(packages, project.Name, catalog.FindItem(tag)!, selected,
            display, 0x101, 8191, "Invalid pair", null, sources));
        Console.WriteLine("investment_sockets private_pool=pass conditions=preserved tail_alias=pass stale=reject reopen=pass");
    }

    /** The same plug in two sockets can carry different unlock conditions and weights. */
    private static void ChoiceMoves(InvestmentCatalog catalog, uint tag)
    {
        byte[] original = new byte[1024];
        Q(original, 0, original.Length); original[186] = 5;
        Q(original, 104, 200 - 104); U(original, 196, 0x808077C1);
        Array(original, 200, 240, 3, 0x808077C4);
        foreach (var (row, count, header) in new[] { (256, 2, 512), (336, 2, 592), (416, 1, 672) })
        {
            S(original, row, 3); S(original, row + 2, row == 336 ? (ushort)2 : (ushort)1);
            S(original, row + 12, ushort.MaxValue); S(original, row + 32, ushort.MaxValue);
            Array(original, row + 64, header, count, 0x80802E03);
        }
        foreach (var (row, plug, condition, argument) in new[]
        {
            (528, 1u, 768, 111u), (560, 2u, 768, 111u), (608, 2u, 816, 222u),
            (640, 3u, 816, 222u), (688, 1u, 864, 333u)
        })
        {
            U(original, row, plug); U(original, row + 4, 0xDEADBEEF);
            U(original, row + 24, argument);
            Array(original, row + 8, condition, 1, 0x80807D31);
            original[condition + 16] = 11; U(original, condition + 20, argument);
        }
        Check(catalog.ReadItem(tag, original).Diagnostics.Count == 0, "The move fixture is invalid.");
        foreach (int[] order in new[] { new[] { 1, 2 }, new[] { 2, 1 } })
        {
            byte[] moved = original;
            foreach (int socket in order)
                moved = catalog.SetSocketPlugs(tag, moved, catalog.ReadItem(tag, moved).Sockets[socket],
                    socket == 1 ? [new(3)] : [new(1), new(2, FromSocket: 1)], socket == 1 ? (ushort)3 : (ushort)1, original);
            int member = Target(moved, 488) + 16 + 32;
            Check(Q(moved, member + 8) == 1 && U(moved, Target(moved, member + 16) + 20) == 222
                && U(moved, member + 24) == 222 && U(moved, member + 4) == 0xDEADBEEF,
                "A moved choice took another socket's condition or weight.");
            Check(catalog.ReadItem(tag, moved).Diagnostics.Count == 0, "A move produced an invalid item.");
        }
        byte[] added = catalog.SetSocketPlugs(tag, original, catalog.ReadItem(tag, original).Sockets[2],
            [new(1), new(2)], 1);
        int plain = Target(added, 488) + 16 + 32;
        Check(Q(added, plain + 8) == 0 && Q(added, plain + 16) == 0 && U(added, plain + 24) == 333,
            "A plain add inherited an unrelated socket's condition or weight.");
        byte[] copied = catalog.SetSocketPlugs(tag, original, catalog.ReadItem(tag, original).Sockets[2],
            [new(1), new(3, Template: 2, FromSocket: 1)], 1);
        int copy = Target(copied, 488) + 16 + 32;
        Check(U(copied, Target(copied, copy + 16) + 20) == 222,
            "A moved project copy lost its source socket's template condition.");
        Reject(() => catalog.SetSocketPlugs(tag, original, catalog.ReadItem(tag, original).Sockets[2],
            [new(1), new(2, FromSocket: 3)], 1));

        byte[] reusable = original.ToArray();
        S(reusable, 416 + 12, 0);
        byte[] retained = catalog.SetSocketPlugs(tag, reusable, catalog.ReadItem(tag, reusable).Sockets[2],
            [new(1), new(2)], 1);
        int kept = Target(retained, 488) + 16 + 32;
        Check(Q(retained, kept + 8) == 1 && U(retained, Target(retained, kept + 16) + 20) == 444
            && U(retained, kept + 24) == 444, "A reusable member lost its condition or weight.");
        Console.WriteLine("socket_choices duplicate_conditions=preserved both_orders=pass plain_add=unconditional reusable=preserved");
    }

    private static byte[] ReusableSet()
    {
        byte[] table = new byte[192]; Q(table, 0, table.Length);
        Array(table, 8, 32, 1, 0x80802DFF); U(table, 48, 1);
        Array(table, 56, 96, 1, 0x80802E03); U(table, 112, 2); U(table, 136, 444);
        Array(table, 120, 160, 1, 0x80807D31); table[176] = 11; U(table, 180, 444);
        return table;
    }

    /** A native catalyst choice keeps its four-instruction condition after the pool grows. */
    public static void Owned(InvestmentCatalog catalog, PackageCatalog packages)
    {
        const uint tag = 0x8132017D;
        const ushort template = 11604, addedPlug = 2288;
        byte[] original = packages.ReadOriginal(tag);
        var before = catalog.ReadItem(tag, original);
        var socket = before.Sockets.Single(row => row.Index == 10);
        Check(catalog.SocketPlugTemplates(tag, original, socket).SequenceEqual(new[] { template }),
            "The native catalyst socket template changed.");
        byte[] added = catalog.AddSocketPlug(tag, original, socket, addedPlug, template);
        var after = catalog.ReadItem(tag, added);
        Check(after.Diagnostics.Count == before.Diagnostics.Count
            && after.Sockets[10].AllowedPlugs.Contains(addedPlug), "The native catalyst socket did not grow.");
        int oldData = Target(original, socket.TypeField.Offset + 72) + 16;
        int newData = Target(added, after.Sockets[10].TypeField.Offset + 72) + 16;
        Check(Q(original, oldData + 8) == 4 && Q(added, newData + 40) == 4
            && Target(original, oldData + 16) == Target(added, newData + 48),
            "The native catalyst condition target changed.");
        Untouched(original, added, socket.TypeField.Offset + 64);
        for (int index = 0; index < before.Sockets.Count; index++)
            if (index != 10) Check(before.Sockets[index].AllowedPlugs.SequenceEqual(after.Sockets[index].AllowedPlugs),
                "Another native socket's choices changed.");
        byte[] selected = catalog.SetSocketInitialPlug(tag, added, after.Sockets[10], addedPlug);
        Check(catalog.ReadItem(tag, selected).Sockets[10].InitialPlug == addedPlug
            && packages.ReadOriginal(tag).SequenceEqual(original), "The native socket edit changed its source.");
        Console.WriteLine("investment_sockets owned=pass catalyst_condition=preserved source=unchanged");
    }

    /** Both descriptors may point at the same member array, including an array at the file end. */
    private static byte[] Weapon(bool conditions)
    {
        byte[] bytes = new byte[conditions ? 640 : 528];
        Q(bytes, 0, bytes.Length); bytes[184] = 0; bytes[186] = 5;
        Q(bytes, 104, 200 - 104); U(bytes, 196, 0x808077C1);
        Array(bytes, 200, 240, 2, 0x808077C4);
        foreach (int row in new[] { 256, 336 })
        {
            S(bytes, row, 3); S(bytes, row + 2, 1);
            S(bytes, row + 12, ushort.MaxValue); S(bytes, row + 32, ushort.MaxValue);
            Array(bytes, row + 64, 448, 2, 0x80802E03);
        }
        U(bytes, 460, 0xA1B2C3D4);
        foreach (var (row, plug) in new[] { (464, 1u), (496, 3u) })
        {
            U(bytes, row, plug); U(bytes, row + 4, 0xDEADBEEF);
            U(bytes, row + 24, 0x3F800000); U(bytes, row + 28, 0x10203040);
            if (conditions) Array(bytes, row + 8, 576, 1, 0x80807D31);
        }
        if (conditions) { bytes[592] = 11; U(bytes, 596, 77); }
        return bytes;
    }

    /** Existing bytes and both shared aliases remain valid after only one descriptor changes. */
    private static void VerifyCopy(InvestmentCatalog catalog, uint tag, byte[] before, byte[] after,
        int conditionTarget)
    {
        var detail = catalog.ReadItem(tag, after);
        Check(detail.Diagnostics.Count == 0 && detail.Sockets[0].AllowedPlugs.Order().SequenceEqual(new ushort[] { 1, 2, 3 })
            && detail.Sockets[1].AllowedPlugs.SequenceEqual(new ushort[] { 1, 3 }), "A shared socket pool changed.");
        Untouched(before, after, 320);
        int data = Target(after, 328) + 16;
        Check(Q(after, 320) == 3 && Q(after, 400) == 2
            && U(after, data - 4) == 0xA1B2C3D4, "Array counts or unknown header bytes changed.");
        for (int index = 0; index < 3; index++)
        {
            int row = data + index * 32;
            Check(U(after, row + 4) == 0xDEADBEEF && U(after, row + 24) == 0x3F800000
                && U(after, row + 28) == 0x10203040, "Unknown member bytes or the weight changed.");
            Check(conditionTarget < 0 ? Q(after, row + 8) == 0 && Q(after, row + 16) == 0
                : Q(after, row + 8) == 1 && Target(after, row + 16) == conditionTarget,
                "A copied member condition changed.");
        }
    }

    private static void Untouched(byte[] before, byte[] after, int descriptor)
    {
        for (int index = 8; index < before.Length; index++)
            if (index < descriptor || index >= descriptor + 16)
                Check(before[index] == after[index], $"Unrelated socket byte {index} changed.");
        Check(Q(after, 0) == after.Length, "The resized item's size header changed.");
    }

    private static byte[] Index(uint firstTag)
    {
        byte[] bytes = new byte[144]; Q(bytes, 0, bytes.Length);
        Array(bytes, 8, 32, 4, 0x80807BE8);
        for (int index = 0; index < 4; index++)
        { U(bytes, 48 + index * 24, (uint)index + 1); U(bytes, 64 + index * 24, firstTag + (uint)index); }
        return bytes;
    }

    private static void Array(byte[] bytes, int field, int header, int count, uint type)
    {
        Q(bytes, field, count); Q(bytes, field + 8, header - field - 8);
        U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type);
    }
    private static int Target(byte[] bytes, int pointer) => checked(pointer + (int)Q(bytes, pointer));
    private static long Q(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static uint U(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return; }
        throw new InvalidOperationException("An invalid socket edit succeeded.");
    }
}
