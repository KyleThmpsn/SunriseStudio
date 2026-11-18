using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/**
 * The shared plug set table. A socket offers its reusable set's members and rolls one from its randomized set.
 * A roll is a selector modulo the member count, so a shared set never grows: an item that needs more gets its own.
 */
public static class InvestmentPlugSets
{
    // Set rows lead with their hash and own a member array. A member leads with its item index and owns a condition
    // array, whose rows are instructions with no pointers.
    private static readonly int SetsField = TagView.FieldOffset<C80802cb6>(nameof(C80802cb6.Table)),
        SetStride = Unsafe.SizeOf<SPlugSetRow>(),
        MembersField = TagView.FieldOffset<SPlugSetRow>(nameof(SPlugSetRow.Entries)),
        MemberStride = Unsafe.SizeOf<PlugSetMember>(),
        ConditionField = TagView.FieldOffset<PlugSetMember>("H9e951b5a.ExpressionSteps"),
        ConditionStride = Unsafe.SizeOf<InvestmentExpressionRow>();
    // The scan reads every item, so it runs once per investment catalog.
    private static readonly ConditionalWeakTable<InvestmentCatalog, IReadOnlySet<int>> rolledSets = new();

    private sealed record Member(int Index, byte[] Row, int At);

    /** A set member's row and its condition array: the count, the element class and the instructions. */
    public sealed record SetMember(byte[] Row, int Conditions, uint ConditionClass, byte[] ConditionRows);

    /** A set to append: its row, then its members with the absolute condition header each names, or -1 for none. */
    private sealed record NewSet(byte[] Row, IReadOnlyList<(byte[] Row, int Condition)> Members);

    /** The sets that list the plug. */
    public static IReadOnlyList<int> SetsWith(ReadOnlyMemory<byte> table, int plug) =>
        [.. Read(table).Select((members, set) => (members, set))
            .Where(row => row.members.Any(member => member.Index == plug)).Select(row => row.set)];

    /** The member of a set that names the plug, or null when the set does not list it. */
    public static SetMember? MemberOf(ReadOnlyMemory<byte> table, int set, int plug)
    {
        var span = table.Span;
        var sets = Sets(span);
        if (set >= sets.Count) throw new InvalidDataException("Socket plug set is outside its index.");
        var members = Members(span, sets.Data + set * SetStride);
        for (int index = 0; index < members.Count; index++)
        {
            int at = members.Data + index * MemberStride;
            if (TagView.At<PlugSetMember>(span, at).PlugItemIndex != plug) continue;
            var conditions = TagArray.Checked(span, at + ConditionField, stride: ConditionStride);
            return new(span.Slice(at, MemberStride).ToArray(), conditions.Count, conditions.Class,
                span.Slice(conditions.Data, conditions.Count * ConditionStride).ToArray());
        }
        return null;
    }

    /** Sets some socket rolls from; a roll picks by member count, so these must not grow. */
    public static IReadOnlySet<int> RolledSets(PackageCatalog catalog, InvestmentCatalog investment) =>
        rolledSets.GetValue(investment, _ =>
        {
            var rolled = new HashSet<int>();
            foreach (var item in investment.Items)
            {
                try
                {
                    ReadOnlySpan<byte> definition = catalog.Read(item.Tag);
                    if (InvestmentCatalog.SocketRows(definition) is not { } sockets) continue;
                    for (int socket = 0; socket < sockets.Count; socket++)
                    {
                        ushort set = Socket(definition, sockets.Data, socket).RandomizedPlugSetRow;
                        if (set != ushort.MaxValue) rolled.Add(set);
                    }
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { }
            }
            return rolled;
        });

    /**
     * Adds `plug` beside `basePlug` in every installed set that lists the base and is not rolled from, unless `listed`
     * is false. Added members carry no condition.
     * @param current The project's table; its installed members must be unchanged.
     * @return The table and the sets that list the plug.
     */
    public static (byte[] Table, IReadOnlyList<int> Sets) Rebuild(ReadOnlyMemory<byte> installed, ReadOnlyMemory<byte> current,
        int plug, int basePlug, bool listed, IReadOnlySet<int> rolled)
    {
        var stock = Read(installed);
        var now = Read(current);
        if (now.Count < stock.Count) throw new InvalidDataException("The plug set table lost sets.");
        int rows = Sets(installed.Span).Data;
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
                members, condition => condition < installed.Length ? condition : throw new InvalidDataException(
                    "A project plug set member names a condition outside the installed table.")))],
                installed.Length);
        return (table, joined);
    }

    /**
     * Gives each socket whose choices are not all in its randomized pool a pool of its own: a copy with those plugs last.
     * @param sockets The sockets an edit changed.
     * @return The definition naming the new sets and the table; a null table when every choice is in its pool.
     */
    public static (byte[] Definition, byte[]? Table) FollowPools(ReadOnlyMemory<byte> definition, ReadOnlyMemory<byte> installed,
        ReadOnlyMemory<byte> current, IEnumerable<int> sockets)
    {
        var item = definition.Span;
        if (InvestmentCatalog.SocketRows(item) is not { } rows) return (definition.ToArray(), null);
        var sets = Read(current);
        var added = new List<NewSet>();
        var pointers = new List<int>();
        foreach (int socket in sockets.Distinct().Order())
        {
            if (socket >= rows.Count) throw new InvalidDataException("The item has no such socket.");
            ref readonly var entry = ref Socket(item, rows.Data, socket);
            ushort pool = entry.RandomizedPlugSetRow;
            if (pool == ushort.MaxValue) continue;
            if (pool >= sets.Count) throw new InvalidDataException("Socket plug set is outside its index.");
            var members = sets[pool];
            // What the item comes with: the initial plug, the embedded members and the reusable set's members.
            ushort initial = entry.InitialPlugItem, reusable = entry.ReusablePlugSetRow;
            var embedded = TagArray.Checked(item, TagView.OffsetOf(item, in entry.EmbeddedPlugs), PlugSetMember.Class,
                MemberStride);
            var choices = new List<int>();
            for (int index = 0; index < embedded.Count; index++)
                choices.Add(checked((int)TagView.At<PlugSetMember>(item, embedded.Data + index * MemberStride).PlugItemIndex));
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
            pointers.Add(TagView.OffsetOf(item, in entry.RandomizedPlugSetRow));
        }
        return added.Count == 0 ? (definition.ToArray(), null)
            : Attach(definition, installed, current, sets.Count, added, pointers);
    }

    /** Gives a socket a pool of exactly these plugs, in roll order, as its own set. A plug the pool holds keeps its member. */
    public static (byte[] Definition, byte[] Table) SetPool(ReadOnlyMemory<byte> definition, ReadOnlyMemory<byte> installed,
        ReadOnlyMemory<byte> current, int socket, IReadOnlyList<ushort> plugs)
    {
        var item = definition.Span;
        if (InvestmentCatalog.SocketRows(item) is not { } rows || socket >= rows.Count)
            throw new InvalidDataException("The item has no such socket.");
        ref readonly var entry = ref Socket(item, rows.Data, socket);
        ushort pool = entry.RandomizedPlugSetRow;
        var sets = Read(current);
        if (pool >= sets.Count || sets[pool].Length == 0)
            throw new InvalidDataException("The socket has no randomized pool to copy.");
        if (plugs.Count == 0) throw new InvalidDataException("A randomized pool needs at least one plug.");
        var members = sets[pool];
        var copy = Copy(current, pool, members, condition => condition);
        var kept = plugs.Select(plug => Array.FindIndex(members, member => member.Index == plug) is var at and >= 0
            ? copy.Members[at] : (Unconditional(members[0].Row, plug), -1)).ToList();
        return Attach(definition, installed, current, sets.Count, [copy with { Members = kept }],
            [TagView.OffsetOf(item, in entry.RandomizedPlugSetRow)]);
    }

    /**
     * Appends sets past the table's last, each with a hash no set uses, and points these randomized plug set fields at
     * them in order.
     */
    private static (byte[] Definition, byte[] Table) Attach(ReadOnlyMemory<byte> definition, ReadOnlyMemory<byte> installed,
        ReadOnlyMemory<byte> current, int count, List<NewSet> added, List<int> pointers)
    {
        var span = current.Span;
        int data = Sets(span).Data;
        var hashes = new HashSet<uint>();
        for (int set = 0; set < count; set++) hashes.Add(TagView.At<SPlugSetRow>(span, data + set * SetStride).Unk0);
        foreach (var set in added)
        {
            var words = set.Members.Select(member => member.Row).Prepend(set.Row)
                .Select(row => BinaryPrimitives.ReadUInt32LittleEndian(row));
            uint hash = StringHash.NewDefinitionHash(string.Join(",", words), hashes);
            hashes.Add(hash);
            TagView.Edit<SPlugSetRow>(set.Row).Unk0 = hash;
        }
        if (count + added.Count > ushort.MaxValue) throw new InvalidDataException("The plug set table has no index left.");
        byte[] table = AppendSets(current, added, installed.Length), edited = definition.ToArray();
        for (int index = 0; index < pointers.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(edited.AsSpan(pointers[index]), checked((ushort)(count + index)));
        return (edited, table);
    }

    /** A copy of a member row naming this plug, always available. */
    private static byte[] Unconditional(byte[] template, int plug)
    {
        byte[] row = template.ToArray();
        ref var member = ref TagView.Edit<PlugSetMember>(row);
        member.PlugItemIndex = checked((uint)plug);
        member.H9e951b5a = default;
        return row;
    }

    /** A set's row and members as a new set; `condition` maps the absolute condition header each member names. */
    private static NewSet Copy(ReadOnlyMemory<byte> table, int set, Member[] members, Func<int, int> condition)
    {
        var span = table.Span;
        int row = Sets(span).Data + set * SetStride;
        var copies = new (byte[] Row, int Condition)[members.Length];
        for (int index = 0; index < members.Length; index++)
        {
            int header = TagPointer.Checked(span, members[index].At + ConditionField + TagArray.RelativeOffset);
            copies[index] = (members[index].Row, header >= 0 ? condition(header) : -1);
        }
        return new(span.Slice(row, SetStride).ToArray(), copies);
    }

    /** Appends whole sets after the last, each with a member array of its own; a member keeps the condition it names. */
    private static byte[] AppendSets(ReadOnlyMemory<byte> table, IReadOnlyList<NewSet> sets, int stockLength)
    {
        var span = table.Span;
        var rows = Sets(span);
        // Member arrays share one class word; the first set with members gives its header words.
        int sample = -1;
        for (int set = 0; set < rows.Count && sample < 0; set++)
        {
            var members = Members(span, rows.Data + set * SetStride);
            if (members.Count > 0) sample = members.Data;
        }
        if (sample < 0) throw new InvalidDataException("The plug set table has no member array to copy.");
        byte[] classData = InvestmentItemClone.ArrayClassData(span, sample).ToArray();
        var blocks = sets.Select(set => TagRelocator.TypedBlock(set.Members.Count, classData,
            set.Members.SelectMany(member => member.Row).ToArray())).ToArray();
        // A new row names no array until its block is placed.
        static byte[] Unlinked(byte[] row)
        {
            byte[] copy = row.ToArray();
            TagView.Edit<SPlugSetRow>(copy).Entries = default;
            return copy;
        }
        var append = new RowAppend(SetsField, SetStride, [.. sets.Select(set => Unlinked(set.Row))])
            { RowArrays = [MembersField], RowSources = [.. sets.Select(_ => -1)] };
        var (output, starts) = InvestmentItemClone.AppendRows(table, [append], blocks, stockLength);
        int grown = Sets(output).Data;
        for (int index = 0; index < sets.Count; index++)
        {
            int row = grown + (rows.Count + index) * SetStride;
            int header = starts[index] + TagRelocator.TypedBlockHeader;
            TagArray.WriteField(output, row + MembersField, sets[index].Members.Count, header);
            for (int member = 0; member < sets[index].Members.Count; member++)
            {
                int pointer = header + TagArray.HeaderSize + member * MemberStride + ConditionField + TagArray.RelativeOffset;
                int target = sets[index].Members[member].Condition;
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pointer), target < 0 ? 0 : target - (long)pointer);
            }
        }
        return output;
    }

    /** Each set's members, in table order. */
    private static List<Member[]> Read(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var sets = Sets(span);
        var result = new List<Member[]>(sets.Count);
        for (int set = 0; set < sets.Count; set++)
        {
            var members = Members(span, sets.Data + set * SetStride);
            var list = new Member[members.Count];
            for (int index = 0; index < members.Count; index++)
            {
                int at = members.Data + index * MemberStride;
                list[index] = new(checked((int)TagView.At<PlugSetMember>(span, at).PlugItemIndex),
                    span.Slice(at, MemberStride).ToArray(), at);
            }
            result.Add(list);
        }
        return result;
    }

    private static (int Count, int Data, uint Class) Sets(ReadOnlySpan<byte> table) =>
        TagArray.Checked(table, SetsField, SPlugSetRow.Class, SetStride);

    /** The member array of the set row at `row`. */
    private static (int Count, int Data, uint Class) Members(ReadOnlySpan<byte> table, int row) =>
        TagArray.Checked(table, row + MembersField, PlugSetMember.Class, MemberStride);

    /** One of an item definition's socket rows, which start at `data`. */
    private static ref readonly ItemOrdinarySocketEntry Socket(ReadOnlySpan<byte> definition, int data, int socket) =>
        ref TagView.At<ItemOrdinarySocketEntry>(definition, data + socket * Unsafe.SizeOf<ItemOrdinarySocketEntry>());
}
