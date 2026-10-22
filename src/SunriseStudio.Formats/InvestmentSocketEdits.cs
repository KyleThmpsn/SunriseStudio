using System.Buffers.Binary;
using System.Globalization;

namespace SunriseStudio.Formats;

/** A choice and its member's original socket; null keeps the destination's member or template. */
public readonly record struct InvestmentSocketChoice(ushort Plug, ushort? Template = null, int? FromSocket = null);

public sealed partial class InvestmentCatalog
{
    /** Ordinary socket blocks hold 80-byte rows and at most 12 instance lanes. */
    private const uint SocketBlockClass = 0x808077C1;
    // Written before each 16-aligned array header.
    private const uint ArrayMarker = 0x80809FBD;
    /** Embedded plug choices carry an item index and an expression array in 32 bytes. */
    private const uint SocketMemberClass = 0x80802E03, SocketExpressionClass = 0x80807D31;
    private const int SocketMemberStride = 32;

    /** Only embedded choices provide a template that stays inside this item. */
    public IReadOnlyList<ushort> SocketPlugTemplates(uint tag, ReadOnlyMemory<byte> current,
        InvestmentSocket socket)
    {
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        return Enumerable.Range(0, members.Count)
            .Select(index => checked((ushort)reader.U32(members.Data + index * SocketMemberStride)))
            .Distinct().ToArray();
    }

    /**
     * A private copy leaves previous socket members and their conditions unchanged.
     * @param preserveTemplateCondition False gives only the new member an empty condition.
     */
    public byte[] AddSocketPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex, ushort templatePlug, bool preserveTemplateCondition = true)
    {
        RequireSocketPlugIndex(plugIndex);
        RequireSocketPlugIndex(templatePlug);
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        if (members.Count == 0)
            throw new InvalidDataException("This socket has no embedded plug choice to copy.");
        int[] templates = Enumerable.Range(0, members.Count)
            .Select(index => members.Data + index * SocketMemberStride)
            .Where(offset => reader.U32(offset) == templatePlug).ToArray();
        if (templates.Length != 1)
            throw new InvalidDataException("Choose one unique template from this socket's embedded plugs.");
        int existing = Enumerable.Range(0, members.Count).Select(index => members.Data + index * SocketMemberStride)
            .FirstOrDefault(offset => reader.U32(offset) == plugIndex, -1);
        if (existing >= 0)
        {
            if (!preserveTemplateCondition && reader.U64(existing + 8) != 0)
                throw new InvalidDataException("The existing plug has a condition. Choose a new plug identity.");
            return current.ToArray();
        }
        if (members.Count >= MaximumAuthoredRows)
            throw new InvalidDataException("This socket has reached the editor's plug limit.");

        int count = checked(members.Count + 1), header = checked((current.Length + 19) & ~15);
        byte[] output = new byte[checked(header + 16 + count * SocketMemberStride)];
        current.Span.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), count);
        current.Span.Slice(members.Data - 8, 8).CopyTo(output.AsSpan(header + 8));
        for (int index = 0; index < count; index++)
        {
            int from = index < members.Count ? members.Data + index * SocketMemberStride : templates[0];
            int to = header + 16 + index * SocketMemberStride;
            current.Span.Slice(from, SocketMemberStride).CopyTo(output.AsSpan(to));
            int expression = reader.Relative(from + 16);
            if (expression >= 0)
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(to + 16), expression - to - 16L);
            if (index == members.Count)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(to), plugIndex);
                if (!preserveTemplateCondition) output.AsSpan(to + 8, 16).Clear();
            }
        }
        int field = row + InvestmentSocketRows.Members;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field), count);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field + 8), header - field - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /** The initial plug must already belong to the socket's declared choices. */
    public byte[] SetSocketInitialPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex)
    {
        RequireSocketPlugIndex(plugIndex);
        ReadSocketForEdit(tag, current, socket);
        if (!socket.AllowedPlugs.Contains(plugIndex))
            throw new InvalidDataException("Add this plug to the socket before making it the initial plug.");
        return Edit(tag, current, socket.PlugField, plugIndex.ToString(CultureInfo.InvariantCulture));
    }

    /** A private socket choice replaces inherited members without changing another socket's pool. */
    public byte[] KeepSocketPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex)
    {
        RequireSocketPlugIndex(plugIndex);
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        int[] matches = Enumerable.Range(0, members.Count)
            .Select(index => members.Data + index * SocketMemberStride)
            .Where(offset => reader.U32(offset) == plugIndex).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The retained plug must have one embedded member in this socket.");
        if (members.Count == 1 && reader.U16(row + InvestmentSocketRows.ReusableSet) == ushort.MaxValue
            && reader.U16(row + InvestmentSocketRows.RandomizedSet) == ushort.MaxValue && socket.InitialPlug == plugIndex)
            return current.ToArray();
        int header = checked((current.Length + 19) & ~15);
        byte[] output = new byte[checked(header + 16 + SocketMemberStride)];
        current.Span.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), 1);
        current.Span.Slice(members.Data - 8, 8).CopyTo(output.AsSpan(header + 8));
        int target = header + 16, source = matches[0];
        current.Span.Slice(source, SocketMemberStride).CopyTo(output.AsSpan(target));
        int expression = reader.Relative(source + 16);
        if (expression >= 0)
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(target + 16), expression - target - 16L);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + InvestmentSocketRows.InitialPlug), plugIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + InvestmentSocketRows.ReusableSet), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + InvestmentSocketRows.RandomizedSet), ushort.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + InvestmentSocketRows.Members), 1);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + InvestmentSocketRows.Members + 8),
            header - row - InvestmentSocketRows.Members - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /**
     * Makes a socket's choices exactly these plugs in this order, with the initial plug among them. A plug keeps the
     * member in its source socket or reusable plug set, weight and condition included; else it copies its embedded
     * template's member and condition, else the first member with no condition.
     * The socket stops drawing from its reusable plug set, so the list is all it comes with. Its randomized pool stays;
     * InvestmentPlugSets.FollowPools adds new choices to it.
     * @param origin The definition before this batch of socket edits, so a plug moved between sockets finds its member
     * whichever socket is rewritten first. Edits only append, so its offsets hold in `current`. Empty uses `current`.
     */
    public byte[] SetSocketPlugs(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        IReadOnlyList<InvestmentSocketChoice> plugs, ushort initial, ReadOnlyMemory<byte> origin = default)
    {
        if (plugs.Count == 0) throw new InvalidDataException("A socket needs at least one plug.");
        if (plugs.Count > MaximumAuthoredRows) throw new InvalidDataException("This socket has reached the editor's plug limit.");
        if (plugs.Select(plug => plug.Plug).Distinct().Count() != plugs.Count)
            throw new InvalidDataException("A plug appears twice in one socket.");
        if (plugs.All(plug => plug.Plug != initial))
            throw new InvalidDataException("The initial plug must be one of the socket's plugs.");
        foreach (var plug in plugs) RequireSocketPlugIndex(plug.Plug);
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        if (members.Count == 0) throw new InvalidDataException("This socket has no embedded plug choice to copy.");
        var before = origin.IsEmpty ? reader : new TagReader(origin);
        var rows = before.Array(before.Relative(InvestmentSocketRows.Field), InvestmentSocketRows.RowClass,
            InvestmentSocketRows.Stride);
        ReadOnlyMemory<byte> table = catalog.ContainsTag(sources.PlugSets) ? catalog.Read(sources.PlugSets) : default;
        int Embedded(TagReader socketReader, int socketRow, ushort item)
        {
            var list = socketReader.Array(socketRow + InvestmentSocketRows.Members, SocketMemberClass, SocketMemberStride);
            return Enumerable.Range(0, list.Count).Select(index => list.Data + index * SocketMemberStride)
                .FirstOrDefault(offset => socketReader.U32(offset) == item, -1);
        }
        // Where each plug's member comes from: an offset in `current`, or a plug set member with its conditions.
        (int At, InvestmentPlugSets.SetMember? Set, bool KeepsCondition) Source(InvestmentSocketChoice choice)
        {
            var socketReader = reader;
            int socketRow = row;
            if (choice.FromSocket is { } from)
            {
                if (from < 0 || from >= rows.Count) throw new InvalidDataException("The plug's source socket no longer exists.");
                socketReader = before;
                socketRow = rows.Data + from * InvestmentSocketRows.Stride;
            }
            if (Embedded(socketReader, socketRow, choice.Plug) is var at and >= 0) return (at, null, true);
            ushort set = socketReader.U16(socketRow + InvestmentSocketRows.ReusableSet);
            if (set != ushort.MaxValue && !table.IsEmpty && InvestmentPlugSets.MemberOf(table, set, choice.Plug) is { } member)
                return (-1, member, true);
            int copied = choice.Template is { } item ? Embedded(socketReader, socketRow, item) : -1;
            var sourceMembers = ReadSocketMembers(socketReader, socketRow);
            if (sourceMembers.Count == 0) throw new InvalidDataException("The plug's source socket has no member to copy.");
            return copied >= 0 ? (copied, null, true) : (sourceMembers.Data, null, false);
        }
        var picks = plugs.Select(Source).ToArray();
        int header = checked((current.Length + 19) & ~15), end = checked(header + 16 + plugs.Count * SocketMemberStride);
        // A plug set member's conditions are copied after the members, each array in a block of its own.
        int[] blocks = new int[plugs.Count];
        for (int index = 0; index < picks.Length; index++)
            if (picks[index].Set is { Conditions: > 0 } member)
            {
                blocks[index] = end;
                end = checked(end + InvestmentItemClone.TypedBlockHeader + 16 + (member.ConditionRows.Length + 15 & ~15));
            }
        byte[] output = new byte[end];
        current.Span.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), plugs.Count);
        current.Span.Slice(members.Data - 8, 8).CopyTo(output.AsSpan(header + 8));
        for (int index = 0; index < plugs.Count; index++)
        {
            var (from, set, keepsCondition) = picks[index];
            int to = header + 16 + index * SocketMemberStride;
            if (set is not null)
            {
                set.Row.CopyTo(output.AsSpan(to));
                output.AsSpan(to + 16, 8).Clear();
                if (set.Conditions != 0)
                {
                    InvestmentItemClone.TypedBlock(set.Conditions, set.ConditionClass, set.ConditionRows)
                        .CopyTo(output.AsSpan(blocks[index]));
                    BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(to + 16),
                        blocks[index] + InvestmentItemClone.TypedBlockHeader - to - 16L);
                }
            }
            else
            {
                current.Span.Slice(from, SocketMemberStride).CopyTo(output.AsSpan(to));
                int expression = reader.Relative(from + 16);
                if (expression >= 0)
                    BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(to + 16), expression - to - 16L);
                // The first member lends its weight only.
                if (!keepsCondition) output.AsSpan(to + 8, 16).Clear();
            }
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(to), plugs[index].Plug);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + InvestmentSocketRows.InitialPlug), initial);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + InvestmentSocketRows.ReusableSet), ushort.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + InvestmentSocketRows.Members), plugs.Count);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + InvestmentSocketRows.Members + 8),
            header - row - InvestmentSocketRows.Members - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    // Definition +64 points to the plug block, which opens with the category hash socket type whitelists name.
    private const int PlugBlockField = 64, PlugCategoryField = 0;
    private const uint PlugBlockClass = 0x808077E3;

    /** The plug category hash of a plug item, or null for an item with no plug block. */
    public static uint? ReadPlugCategory(ReadOnlyMemory<byte> definition)
    {
        var reader = new TagReader(definition);
        if (!reader.Has(PlugBlockField, 8)) return null;
        int block = reader.Relative(PlugBlockField);
        if (block < 4 || !reader.Has(block, PlugCategoryField + 4)) return null;
        if (reader.U32(block - 4) != PlugBlockClass) throw new InvalidDataException("Item plug block has an unsupported layout.");
        return reader.U32(block + PlugCategoryField);
    }

    /** The caller's socket must still describe the current native row. */
    private (TagReader Reader, int Row) ReadSocketForEdit(uint tag, ReadOnlyMemory<byte> current,
        InvestmentSocket socket)
    {
        if (FindItem(tag) is not { } item || item.Tag != tag)
            throw new InvalidDataException("Open an item definition to change its sockets.");
        RequireClass(catalog, tag, ItemClass);
        var reader = new TagReader(current);
        if (!reader.Has(0, 188) || reader.U64(0) != (ulong)current.Length)
            throw new InvalidDataException("Item size header is invalid.");
        int block = reader.Relative(InvestmentSocketRows.Field);
        if (block < 4 || !reader.Has(block, 32) || reader.U32(block - 4) != SocketBlockClass)
            throw new InvalidDataException("Item socket block has an unsupported layout.");
        var rows = reader.Array(block, InvestmentSocketRows.RowClass, InvestmentSocketRows.Stride);
        if (rows.Count > 12 || socket.Index < 0 || socket.Index >= rows.Count)
            throw new InvalidDataException("Socket is outside the item's 12 ordinary lanes.");
        int row = rows.Data + socket.Index * InvestmentSocketRows.Stride;
        var detail = ReadItem(tag, current);
        var actual = detail.Sockets.FirstOrDefault(value => value.Index == socket.Index);
        if (actual is null || actual.Type == ushort.MaxValue || actual.TypeField != socket.TypeField
            || actual.PlugField != socket.PlugField || actual.TypeField.Offset != row
            || actual.PlugField.Offset != row + InvestmentSocketRows.InitialPlug || actual.Type != socket.Type
            || actual.InitialPlug != socket.InitialPlug || !actual.AllowedPlugs.SequenceEqual(socket.AllowedPlugs))
            throw new InvalidDataException("This socket changed or is empty. Reopen its editor.");
        if (detail.Diagnostics.Any(message => message.StartsWith("Socket", StringComparison.Ordinal)))
            throw new InvalidDataException("The item's socket lists are malformed.");
        return (reader, row);
    }

    /** Moved members keep their expressions at the original byte offsets. */
    private (int Count, int Data, uint Class) ReadSocketMembers(TagReader reader, int row)
    {
        int field = row + InvestmentSocketRows.Members;
        var members = reader.Array(field, SocketMemberClass, SocketMemberStride);
        if (members.Count > MaximumAuthoredRows)
            throw new InvalidDataException("This socket has too many embedded plugs for the editor.");
        if (members.Count == 0 && reader.I64(field + 8) != 0)
            throw new InvalidDataException("An empty socket plug array has an unsupported pointer.");
        for (int index = 0; index < members.Count; index++)
        {
            int member = members.Data + index * SocketMemberStride;
            uint plug = reader.U32(member);
            if (plug >= InvestmentItemClone.MaximumIndexRows || plug >= Items.Count)
                throw new InvalidDataException("A socket choice names an unreachable item index.");
            var expression = reader.Array(member + 8, SocketExpressionClass, 8);
            if (expression.Count > MaximumAuthoredRows
                || expression.Count == 0 && reader.I64(member + 16) != 0)
                throw new InvalidDataException("A socket choice has an unsupported condition array.");
        }
        return members;
    }

    private void RequireSocketPlugIndex(ushort index)
    {
        if (index >= InvestmentItemClone.MaximumIndexRows || index >= Items.Count)
            throw new InvalidDataException("Choose a reachable item from this project's catalog.");
        RequireClass(catalog, Items[index].Tag, ItemClass);
    }
}
