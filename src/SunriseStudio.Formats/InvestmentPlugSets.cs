using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/**
 * The shared plug set table. A socket offers the members of the reusable set it names at socket row +12 and rolls one
 * from the randomized set at +32; a member's condition, when it has one, unlocks it. A roll is a selector modulo the
 * set's member count, so a set other items roll from never grows; an item whose pool needs more gets a set of its own.
 */
public static class InvestmentPlugSets
{
    // Set rows: {u32 hash, member array at +8}. Members: {u32 item index, condition array at +8, f32 weight at +24}.
    private const int SetsField = 8, SetStride = 24, MembersField = 8, MemberStride = 32, ConditionField = 8;
    private const uint SetClass = 0x80802DFF, MemberClass = 0x80802E03;
    // The 32-bit FNV prime, which mixes a new set's members into its hash.
    private const uint FnvPrime = 0x01000193;
    // The scan reads every item, so it runs once per investment catalog.
    private static readonly ConditionalWeakTable<InvestmentCatalog, IReadOnlySet<int>> rolledSets = new();

    private sealed record Member(int Index, byte[] Row, int At);

    /** A set to append: its row, then its members with the absolute condition each names, or -1 for none. */
    private sealed record NewSet(byte[] Row, IReadOnlyList<(byte[] Row, int Condition)> Members);

    /** The sets that list the plug. */
    public static IReadOnlyList<int> SetsWith(ReadOnlyMemory<byte> table, int plug) =>
        [.. Read(table).Select((members, set) => (members, set))
            .Where(row => row.members.Any(member => member.Index == plug)).Select(row => row.set)];

    /** Sets some socket rolls from; a roll picks by member count, so these must not grow. */
    public static IReadOnlySet<int> RolledSets(PackageCatalog catalog, InvestmentCatalog investment) =>
        rolledSets.GetValue(investment, _ =>
        {
            var rolled = new HashSet<int>();
            foreach (var item in investment.Items)
            {
                try
                {
                    var reader = new TagReader(catalog.Read(item.Tag));
                    int block = reader.Relative(InvestmentSocketRows.Field);
                    if (block < 0) continue;
                    var sockets = reader.Array(block, InvestmentSocketRows.RowClass, InvestmentSocketRows.Stride);
                    for (int socket = 0; socket < sockets.Count; socket++)
                    {
                        ushort set = reader.U16(sockets.Data + socket * InvestmentSocketRows.Stride + InvestmentSocketRows.RandomizedSet);
                        if (set != ushort.MaxValue) rolled.Add(set);
                    }
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { }
            }
            return rolled;
        });

    /**
     * The installed table with the members the project added, and `plug` beside `basePlug` in every set that lists the base
     * and is not rolled from; with `listed` false, `plug` is in no set. Added members carry no condition.
     * @param current The project's table; its installed members must be unchanged.
     * @return The table and the sets that list the plug.
     */
    public static (byte[] Table, IReadOnlyList<int> Sets) Rebuild(ReadOnlyMemory<byte> installed, ReadOnlyMemory<byte> current,
        int plug, int basePlug, bool listed, IReadOnlySet<int> rolled)
    {
        var stock = Read(installed);
        var now = Read(current);
        if (now.Count < stock.Count) throw new InvalidDataException("The plug set table lost sets.");
        int rows = new TagReader(installed).Array(SetsField, SetClass, SetStride).Data;
        var appends = new List<RowAppend>();
        var joined = new List<int>();
        for (int set = 0; set < stock.Count; set++)
        {
            int kept = stock[set].Length;
            if (now[set].Length < kept || !now[set].Take(kept).Select(member => member.Index)
                    .SequenceEqual(stock[set].Select(member => member.Index)))
                throw new InvalidDataException($"Plug set {set} changed its installed members.");
            var added = now[set].Skip(kept).Where(member => member.Index != plug).Select(member => member.Row).ToList();
            if (listed && !rolled.Contains(set) && stock[set].All(member => member.Index != plug)
                && stock[set].FirstOrDefault(member => member.Index == basePlug) is { } template)
            {
                // The base's own row, weight included.
                added.Add(Unconditional(template.Row, plug));
                joined.Add(set);
            }
            if (added.Count != 0)
                appends.Add(new(rows + set * SetStride + MembersField, MemberStride, added) { RowArrays = [ConditionField] });
        }
        byte[] table = InvestmentItemClone.AppendRows(installed, appends, stockLength: installed.Length).Table;
        // Sets the project added, such as an item's own pool, keep their members and conditions.
        if (now.Count > stock.Count)
            table = AppendSets(table, [.. now.Skip(stock.Count).Select((members, index) => Copy(current, stock.Count + index,
                members, condition => condition < installed.Length ? condition
                    : throw new InvalidDataException("A project plug set member names a condition outside the installed table.")))],
                installed.Length);
        return (table, joined);
    }

    /**
     * Gives each listed socket whose choices are not all in its randomized pool a pool of its own: a copy of the pool
     * with those plugs added last, appended as a new set. Added members carry no condition.
     * @param sockets The sockets an edit changed.
     * @return The definition naming the new sets and the table; a null table when every choice is in its pool.
     */
    public static (byte[] Definition, byte[]? Table) FollowPools(ReadOnlyMemory<byte> definition, ReadOnlyMemory<byte> installed,
        ReadOnlyMemory<byte> current, IEnumerable<int> sockets)
    {
        var item = new TagReader(definition);
        int block = item.Relative(InvestmentSocketRows.Field);
        if (block < 0) return (definition.ToArray(), null);
        var rows = item.Array(block, InvestmentSocketRows.RowClass, InvestmentSocketRows.Stride);
        var sets = Read(current);
        var added = new List<NewSet>();
        var pointers = new List<int>();
        foreach (int socket in sockets.Distinct().Order())
        {
            if (socket >= rows.Count) throw new InvalidDataException("The item has no such socket.");
            int row = rows.Data + socket * InvestmentSocketRows.Stride;
            ushort pool = item.U16(row + InvestmentSocketRows.RandomizedSet);
            if (pool == ushort.MaxValue) continue;
            if (pool >= sets.Count) throw new InvalidDataException("Socket plug set is outside its index.");
            var members = sets[pool];
            // What the item comes with: the initial plug, the embedded members and the reusable set's members.
            ushort initial = item.U16(row + InvestmentSocketRows.InitialPlug), reusable = item.U16(row + InvestmentSocketRows.ReusableSet);
            var embedded = item.Array(row + InvestmentSocketRows.Members, MemberClass, MemberStride);
            var choices = Enumerable.Range(0, embedded.Count)
                .Select(index => checked((int)item.U32(embedded.Data + index * MemberStride))).ToList();
            if (initial != ushort.MaxValue) choices.Add(initial);
            if (reusable != ushort.MaxValue)
            {
                if (reusable >= sets.Count) throw new InvalidDataException("Socket plug set is outside its index.");
                choices.AddRange(sets[reusable].Select(member => member.Index));
            }
            int[] missing = [.. choices.Distinct().Where(plug => members.All(member => member.Index != plug))];
            if (missing.Length == 0 || members.Length == 0) continue;
            var copy = Copy(current, pool, members, condition => condition);
            // The pool's first member's row.
            var extra = missing.Select(plug => (Unconditional(members[0].Row, plug), -1));
            added.Add(copy with { Members = [.. copy.Members, .. extra] });
            pointers.Add(row);
        }
        if (added.Count == 0) return (definition.ToArray(), null);
        // Each new set takes a hash no set uses.
        var reader = new TagReader(current);
        int data = reader.Array(SetsField, SetClass, SetStride).Data;
        var hashes = new HashSet<uint>(Enumerable.Range(0, sets.Count).Select(set => reader.U32(data + set * SetStride)));
        foreach (var set in added)
        {
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(set.Row);
            foreach (var (member, _) in set.Members) hash = (hash ^ BinaryPrimitives.ReadUInt32LittleEndian(member)) * FnvPrime;
            while (hash == 0 || !hashes.Add(hash)) hash++;
            BinaryPrimitives.WriteUInt32LittleEndian(set.Row, hash);
        }
        if (sets.Count + added.Count > ushort.MaxValue) throw new InvalidDataException("The plug set table has no index left.");
        byte[] table = AppendSets(current, added, installed.Length), edited = definition.ToArray();
        for (int index = 0; index < pointers.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(edited.AsSpan(pointers[index] + InvestmentSocketRows.RandomizedSet),
                checked((ushort)(sets.Count + index)));
        return (edited, table);
    }

    /** A copy of a member row naming this plug, always available. */
    private static byte[] Unconditional(byte[] template, int plug)
    {
        byte[] row = template.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(row, checked((uint)plug));
        row.AsSpan(ConditionField, 16).Clear();
        return row;
    }

    /** A set's row and members as a new set; `condition` maps the absolute condition each member names. */
    private static NewSet Copy(ReadOnlyMemory<byte> table, int set, Member[] members, Func<int, int> condition)
    {
        var reader = new TagReader(table);
        int row = reader.Array(SetsField, SetClass, SetStride).Data + set * SetStride;
        return new(table.Span.Slice(row, SetStride).ToArray(), [.. members.Select(member =>
            (member.Row, reader.Relative(member.At + ConditionField + 8) is var target and >= 0 ? condition(target) : -1))]);
    }

    /** Appends whole sets after the last, each with a member array of its own; a member keeps the condition it names. */
    private static byte[] AppendSets(ReadOnlyMemory<byte> table, IReadOnlyList<NewSet> sets, int stockLength)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(SetsField, SetClass, SetStride);
        // Member arrays share one class word; the first set with members gives its header bytes.
        int sample = Enumerable.Range(0, rows.Count)
            .Select(set => reader.Array(rows.Data + set * SetStride + MembersField, MemberClass, MemberStride))
            .First(members => members.Count != 0).Data;
        byte[] classData = table.Span.Slice(sample - 8, 8).ToArray();
        var blocks = sets.Select(set => InvestmentItemClone.TypedBlock(set.Members.Count, classData,
            set.Members.SelectMany(member => member.Row).ToArray())).ToArray();
        // A new row names no array until its block is placed.
        byte[] Unlinked(byte[] row)
        {
            byte[] copy = row.ToArray();
            copy.AsSpan(MembersField, 16).Clear();
            return copy;
        }
        var append = new RowAppend(SetsField, SetStride, [.. sets.Select(set => Unlinked(set.Row))])
            { RowArrays = [MembersField], RowSources = [.. sets.Select(_ => -1)] };
        var (output, starts) = InvestmentItemClone.AppendRows(table, [append], blocks, stockLength);
        var grown = new TagReader(output).Array(SetsField, SetClass, SetStride);
        for (int index = 0; index < sets.Count; index++)
        {
            int row = grown.Data + (rows.Count + index) * SetStride, header = starts[index] + InvestmentItemClone.TypedBlockHeader;
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + MembersField), sets[index].Members.Count);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + MembersField + 8), header - row - MembersField - 8L);
            for (int member = 0; member < sets[index].Members.Count; member++)
            {
                int pointer = header + 16 + member * MemberStride + ConditionField + 8;
                int target = sets[index].Members[member].Condition;
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pointer), target < 0 ? 0 : target - (long)pointer);
            }
        }
        return output;
    }

    /** Each set's members, in table order. */
    private static List<Member[]> Read(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var sets = reader.Array(SetsField, SetClass, SetStride);
        var result = new List<Member[]>(sets.Count);
        for (int set = 0; set < sets.Count; set++)
        {
            var members = reader.Array(sets.Data + set * SetStride + MembersField, MemberClass, MemberStride);
            result.Add([.. Enumerable.Range(0, members.Count).Select(index => members.Data + index * MemberStride)
                .Select(at => new Member(checked((int)reader.U32(at)), table.Span.Slice(at, MemberStride).ToArray(), at))]);
        }
        return result;
    }
}
