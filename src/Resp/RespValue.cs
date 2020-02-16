﻿using Resp.Internal;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Resp
{

    public enum RespType : byte
    {
        // \0$+-:_,#!=(*%~|>
        Unknown = 0,
        BlobString = (byte)'$',
        SimpleString = (byte)'+',
        SimpleError = (byte)'-',
        Number = (byte)':',
        Null = (byte)'_',
        Double = (byte)',',
        Boolean = (byte)'#',
        BlobError = (byte)'!',
        VerbatimString = (byte)'=',
        BigNumber = (byte)'(',
        Array = (byte)'*',
        Map = (byte)'%',
        Set = (byte)'~',
        Attribute = (byte)'|',
        Push = (byte)'>',
    }

    internal enum StorageKind : byte
    {
        Unknown,
        Null,
        Empty,
        InlinedBytes, // overlapped = payload, aux = length
        InlinedUInt32, // overlapped = payload
        InlinedInt64, // overlapped = payload
        InlinedDouble, // overlapped = payload
        ArraySegmentByte, // overlapped = offset/length, obj0 = array
        ArraySegmentChar, // overlapped = offset/length, obj0 = array
        StringSegment, // overlapped = offset/length, obj0 = value
        Utf8StringSegment, // overlapped = offset/length, obj0 = value
        MemoryManagerByte, // overlapped = offset/length, obj0 = manager
        MemoryManagerChar, // overlapped = offset/length, obj0 = manager
        SequenceSegmentByte, // overlapped = offset/length, obj0 = memory owner
        SequenceSegmentChar, // overlapped = start index/end index, obj0 = start segment, obj1 = end segment

        ArraySegmentRespValue, // overlapped = offset/length, obj0 = array
        MemoryManagerRespValue, // overlapped = offset/length, obj0 = manager
        SequenceSegmentRespValue, // overlapped = start index/end index, obj0 = start segment, obj1 = end segment
    }

    public readonly partial struct RespValue
    {
        private readonly State _state;
        private readonly object _obj0, _obj1;

        public static readonly RespValue Ping = Command("PING");

        public RespType Type => _state.Type;

        private static Encoding ASCII => Encoding.ASCII;
        private static Encoding UTF8 => Encoding.UTF8;

        public override string ToString()
        {
            if (TryGetChars(out var chars))
            {
                if (chars.IsSingleSegment)
                {
                    if (MemoryMarshal.TryGetString(chars.First, out var s,
                        out var start, out var length))
                    {
                        return s.Substring(start, length);
                    }
                    return new string(chars.First.Span);
                }
            }
            if (TryGetBytes(out var bytes))
            {
                if (bytes.IsSingleSegment)
                    return UTF8.GetString(bytes.First.Span);
            }
            switch (_state.Storage)
            {
                case StorageKind.Null:
                    return "";
                case StorageKind.InlinedBytes:
                    return UTF8.GetString(_state.AsSpan());
                case StorageKind.InlinedInt64:
                    return _state.Int64.ToString(NumberFormatInfo.InvariantInfo);
                case StorageKind.InlinedUInt32:
                    return _state.UInt32.ToString(NumberFormatInfo.InvariantInfo);
                case StorageKind.InlinedDouble:
                    var r64 = _state.Double;
                    if (double.IsInfinity(r64))
                    {
                        if (double.IsPositiveInfinity(r64)) return "+inf";
                        if (double.IsNegativeInfinity(r64)) return "-inf";
                    }
                    return r64.ToString("G17", NumberFormatInfo.InvariantInfo);
                default:
                    ThrowHelper.StorageKindNotImplemented(_state.Storage);
                    return default;
            }
        }

        private RespValue(State state, object obj0 = null, object obj1 = null)
        {
            _state = state;
            _obj0 = obj0;
            _obj1 = obj1;
        }

        public static Lifetime<Memory<RespValue>> Lease(int length)
        {
            if (length < 0) ThrowHelper.ArgumentOutOfRange(nameof(length));
            if (length == 0) return default;
            var arr = ArrayPool<RespValue>.Shared.Rent(length);

            var memory = new Memory<RespValue>(arr, 0, length);
            memory.Span.Clear();
            return new Lifetime<Memory<RespValue>>(memory, (_, state) => ArrayPool<RespValue>.Shared.Return((RespValue[])state), arr);
        }

        private static RespValue Command(string command)
        {
            var len = ASCII.GetByteCount(command);
            if (len <= State.InlineSize)
            {
                var state = new State((byte)len, RespType.Array, RespType.BlobString);
                ASCII.GetBytes(command.AsSpan(), state.AsWritableSpan());
                return new RespValue(state);
            }
            else
            {
                var arr = ASCII.GetBytes(command); // pre-encode
                return new RespValue(new State(RespType.Array,
                    StorageKind.ArraySegmentByte, 0, len, RespType.BlobString), arr);
            }
        }

        private static RespValue Create(RespType type, ReadOnlySequence<byte> payload)
        {
            var len = payload.Length;
            if (len == 0)
            {
                return new RespValue(new State(type, StorageKind.Empty, 0, 0));
            }
            else if (len <= State.InlineSize)
            {
                var state = new State((byte)len, type);
                if (len != 0) payload.CopyTo(state.AsWritableSpan());
                return new RespValue(state);
            }
            else if (payload.IsSingleSegment)
            {
                var memory = payload.First;
                if (MemoryMarshal.TryGetArray(memory, out var segment))
                {
                    return new RespValue(
                        new State(type, StorageKind.ArraySegmentByte, segment.Offset, segment.Count),
                        segment.Array);
                }
                else if (MemoryMarshal.TryGetMemoryManager(memory, out MemoryManager<byte> manager, out var start, out var length))
                {
                    return new RespValue(
                        new State(type, StorageKind.MemoryManagerByte, start, length),
                        manager);
                }
            }
            SequencePosition seqStart = payload.Start, seqEnd = payload.End;
            if (seqStart.GetObject() is ReadOnlySequenceSegment<byte> segStart
                && seqEnd.GetObject() is ReadOnlySequenceSegment<byte> segEnd)
            {
                return new RespValue(
                    new State(type, StorageKind.SequenceSegmentByte,
                    seqStart.GetInteger(), seqEnd.GetInteger()), segStart, segEnd);
            }
            ThrowHelper.UnknownSequenceVariety();
            return default;
        }

        public void Write(IBufferWriter<byte> output)
        {
            if (IsAggregate(_state.Type))
            {
                WriteAggregate(output);
            }
            else
            {
                WriteValue(output, _state.Type);
            }
        }

        private void WriteAggregate(IBufferWriter<byte> output)
        {
            switch (_state.Storage)
            {
                case StorageKind.Null:
                    WriteNull(output, _state.Type);
                    break;
                case StorageKind.Empty:
                    WriteEmpty(output, _state.Type);
                    break;
                case StorageKind.InlinedBytes when IsBlob(_state.SubType):
                    WriteUnitAggregateInlinedBlob(output);
                    break;
                default:
                    if (IsUnitAggregate(out var value))
                    {
                        WriteUnitAggregate(output, Type, in value);
                    }
                    else
                    {
                        WriteAggregate(output, Type, GetSubValues());
                    }
                    break;
            }

            
            //    case StorageKind.ArraySegmentRespValue:
            //        Write(output, _state.Type, new ReadOnlySpan<RespValue>((RespValue[])_obj0,
            //            _state.StartOffset, _state.Length));
            //        break;
            //    case StorageKind.MemoryManagerRespValue:
            //        Write(output, _state.Type, ((MemoryManager<RespValue>)_obj0).Memory.Span
            //            .Slice(_state.StartOffset, _state.Length));
            //        break;
            //    case StorageKind.SequenceSegmentRespValue:
            //        ThrowHelper.StorageKindNotImplemented(_state.Storage);
            //        break;
            //    default:
            //        WriteUnitAggregate(output);
            //        break;
            //}
        }

        public ReadOnlySequence<RespValue> GetSubValues()
        {
            switch (_state.Storage)
            {
                case StorageKind.Null:
                case StorageKind.Empty:
                    return default;
                case StorageKind.ArraySegmentRespValue:
                    return new ReadOnlySequence<RespValue>((RespValue[])_obj0,
                        _state.StartOffset, _state.Length);
                case StorageKind.MemoryManagerRespValue:
                    return new ReadOnlySequence<RespValue>(
                        ((MemoryManager<RespValue>)_obj0).Memory
                            .Slice(_state.StartOffset, _state.Length));
                case StorageKind.SequenceSegmentRespValue:
                    return new ReadOnlySequence<RespValue>(
                        (ReadOnlySequenceSegment<RespValue>)_obj0,
                        _state.StartOffset,
                        (ReadOnlySequenceSegment<RespValue>)_obj1,
                        _state.EndOffset);
                default:
                    if (_state.IsInlined && _state.SubType != RespType.Unknown)
                    {
                        ThrowHelper.Invalid("This aggregate must be accessed via " + nameof(IsUnitAggregate));
                    }
                    ThrowHelper.StorageKindNotImplemented(_state.Storage);
                    return default;
            }
        }
        public bool IsUnitAggregate(out RespValue value)
        {
            if (IsAggregate(_state.Type))
            {
                switch(_state.Storage)
                {
                    case StorageKind.Empty:
                    case StorageKind.Null:
                        break;
                    case StorageKind.ArraySegmentRespValue:
                        if (_state.Length == 1)
                        {
                            value = ((RespValue[])_obj0)[_state.StartOffset];
                            return true;
                        }
                        break;
                    case StorageKind.MemoryManagerRespValue:
                        if (_state.Length == 1)
                        {
                            value = ((MemoryManager<RespValue>)_obj0).GetSpan()[_state.StartOffset];
                            return true;
                        }
                        break;
                    case StorageKind.SequenceSegmentRespValue:
                        var seq = new ReadOnlySequence<RespValue>(
                            (ReadOnlySequenceSegment<RespValue>)_obj0, _state.StartOffset,
                            (ReadOnlySequenceSegment<RespValue>)_obj1, _state.EndOffset);
                        if (seq.Length == 1)
                        {
                            value = seq.FirstSpan[0];
                            return true;
                        }
                        break;
                    default:
                        if (_state.IsInlined && _state.SubType != RespType.Unknown)
                        {
                            value = new RespValue(_state.Unwrap());
                            return true;
                        }
                        break;
                }
            }
            value = default;
            return false;
        }

        private static void WriteAggregate(IBufferWriter<byte> output, RespType aggregateType, ReadOnlySequence<RespValue> values)
        {
            // {type}{count}\r\n
            // {payload0}\r\n
            // {payload1}\r\n
            // {payload...}\r\n

            var span = output.GetSpan(24);
            span[0] = GetPrefix(aggregateType);
            if (!Utf8Formatter.TryFormat((ulong)values.Length, span.Slice(1), out var count))
                ThrowHelper.Format();
            count++; // for the prefix
            span[count++] = (byte)'\r';
            span[count++] = (byte)'\n';
            output.Advance(count);
            foreach (var segment in values)
            {
                foreach (ref readonly RespValue value in segment.Span)
                {
                    value.Write(output);
                }
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //private static bool EqualsShortAlphaIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowerCaseValue)
        //{
        //    return value.Length switch
        //    {

        //        // to lower-casify; note that for non-alpha, this is an invalid thing, so
        //        // this should *only* be used when the comparison value is known to be alpha

        //        // OK
        //        2 => (BitConverter.ToInt16(value) | 0x2020) == BitConverter.ToInt16(lowerCaseValue),
        //        // PONG
        //        4 => (BitConverter.ToInt32(value) | 0x20202020) == BitConverter.ToInt32(lowerCaseValue),
        //        _ => EqualsSlow(value, lowerCaseValue),
        //    };

        //    static bool EqualsSlow(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowerCaseValue)
        //    {
        //        // compare in 8-byte chunks as var as possible; could also look at SIMD, but...
        //        // how large values are we expecting, really?
        //        var value64 = MemoryMarshal.Cast<byte, long>(value);
        //        var lowerCaseValue64 = MemoryMarshal.Cast<byte, long>(lowerCaseValue);
        //        for (int i = 0; i < value64.Length; i++)
        //        {
        //            if ((value64[i] | 0x2020202020202020) != lowerCaseValue64[i]) return false;
        //        }
        //        for (int i = value64.Length * 8; i < value.Length; i++)
        //        {
        //            if ((value[i] | 0x20) != lowerCaseValue[i]) return false;
        //        }
        //        return true;
        //    }
        //}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsShortAlphaIgnoreCase(in RespValue other)
        {
            ref readonly State x = ref _state, y = ref other._state;
            // 0x20 is 00100000, which is the bit which **for purely alpha** can be used
            return x.Storage == StorageKind.InlinedBytes
                & y.Storage == StorageKind.InlinedBytes
                & (x.Int64 | 0x2020202020202020) == (y.Int64 | 0x2020202020202020)
                & (x.HighInt32 | 0x20202020) == (y.HighInt32 | 0x20202020);
        }

        private void WriteValue(IBufferWriter<byte> output, RespType type)
        {
            switch (type)
            {
                case RespType.Null:
                    WriteNull(output, type);
                    break;
                case RespType.BlobString:
                case RespType.BlobError:
                case RespType.VerbatimString:
                    WriteBlob(output, type);
                    break;
                case RespType.SimpleError:
                case RespType.SimpleString:
                case RespType.Number:
                case RespType.BigNumber:
                case RespType.Boolean:
                case RespType.Double:
                    WriteLineTerminated(output, type);
                    break;
                default:
                    ThrowHelper.RespTypeNotImplemented(type);
                    break;
            }
        }

        private void WriteLineTerminated(IBufferWriter<byte> output, RespType type)
        {
            // {type}{payload}\r\n
            if (_state.IsInlined)
            {
                Span<byte> span;
                int len;
                switch (_state.Storage)
                {
                    case StorageKind.InlinedBytes:
                        len = _state.PayloadLength;
                        span = output.GetSpan(len + 3);
                        _state.AsSpan().CopyTo(span.Slice(1));
                        break;
                    case StorageKind.InlinedDouble:
                        span = output.GetSpan(70);
                        if (!Utf8Formatter.TryFormat(_state.Double, span.Slice(1), out len))
                            ThrowHelper.Format();
                        break;
                    case StorageKind.InlinedInt64:
                        span = output.GetSpan(23);
                        if (!Utf8Formatter.TryFormat(_state.Int64, span, out len))
                            ThrowHelper.Format();
                        break;
                    case StorageKind.InlinedUInt32:
                        span = output.GetSpan(23);
                        if (!Utf8Formatter.TryFormat(_state.UInt32, span, out len))
                            ThrowHelper.Format();
                        break;
                    default:
                        ThrowHelper.StorageKindNotImplemented(_state.Storage);
                        span = default;
                        len = default;
                        break;
                }
                span[0] = GetPrefix(type);
                span[len + 1] = (byte)'\r';
                span[len + 2] = (byte)'\n';
                output.Advance(len + 3);
            }
            else
            {
                switch(_state.Storage)
                {
                    case StorageKind.Null: // treat like empty in this case
                    case StorageKind.Empty:
                        WriteEmpty(output, type);
                        break;
                    default:
                        output.GetSpan(1)[0] = GetPrefix(type);
                        output.Advance(1);
                        if (TryGetBytes(out var bytes))
                        {
                            foreach (var segment in bytes)
                                output.Write(segment.Span);
                        }
                        else if (TryGetChars(out var chars))
                        {
                            foreach (var segment in chars)
                                WriteUtf8(output, segment.Span);
                        }
                        else
                        {
                            ThrowHelper.StorageKindNotImplemented(_state.Storage);
                        }
                        output.Write(NewLine);
                        break;
                }
            }
        }
        private void WriteBlob(IBufferWriter<byte> output, RespType type)
        {
            // {type}{length}\r\n
            // {payload}\r\n
            // unless null, which is
            // {type}-1\r\n
            switch (_state.Storage)
            {
                case StorageKind.Null:
                    WriteNull(output, type);
                    break;
                case StorageKind.Empty:
                    WriteEmpty(output, type);
                    break;
                case StorageKind.InlinedBytes:
                    var span = output.GetSpan(State.InlineSize + 7);
                    int offset = 0;
                    span[offset++] = GetPrefix(type);
                    int len = _state.PayloadLength;
                    if (len < 10)
                    {
                        span[offset++] = (byte)('0' + len);
                    }
                    else
                    {
                        span[offset++] = (byte)('1');
                        span[offset++] = (byte)(('0' - 10) + len);
                    }
                    span[offset++] = (byte)'\r';
                    span[offset++] = (byte)'\n';
                    _state.AsSpan().CopyTo(span.Slice(offset));
                    offset += len;
                    span[offset++] = (byte)'\r';
                    span[offset++] = (byte)'\n';
                    output.Advance(offset);
                    break;
                default:
                    if (TryGetChars(out var chars))
                    {
                        WriteLengthPrefix(output, type, CountUtf8(chars));
                        WriteUtf8(output, chars);
                        output.Write(NewLine);
                    }
                    else if (TryGetBytes(out var bytes))
                    {
                        WriteLengthPrefix(output, type, bytes.Length);
                        Write(output, bytes);
                        output.Write(NewLine);
                    }
                    else
                    {
                        ThrowHelper.StorageKindNotImplemented(_state.Storage);
                    }
                    break;
            }

            static void WriteLengthPrefix(IBufferWriter<byte> output, RespType type, long length)
            {
                // {type}{length}\r\n
                var span = output.GetSpan(24);
                span[0] = GetPrefix(type);
                if (!Utf8Formatter.TryFormat((ulong)length, span.Slice(1), out var bytes))
                    ThrowHelper.Format();
                bytes++; // for the prefix
                span[bytes++] = (byte)'\r';
                span[bytes++] = (byte)'\n';
                output.Advance(bytes);
            }
        }
        long CountUtf8(in ReadOnlySequence<char> payload)
        {
            if (payload.IsSingleSegment)
            {
                return UTF8.GetByteCount(payload.FirstSpan);
            }
            else
            {
                long count = 0;
                foreach (var segment in payload)
                    count += UTF8.GetByteCount(segment.Span);
                return count;
            }
        }
        private static void WriteUtf8(IBufferWriter<byte> output, in ReadOnlySequence<char> payload)
        {
            if (payload.IsSingleSegment)
            {
                WriteUtf8(output, payload.FirstSpan);
            }
            else
            {
                foreach (var segment in payload)
                    WriteUtf8(output, segment.Span);
            }
        }

        private static void Write(IBufferWriter<byte> output, in ReadOnlySequence<byte> payload)
        {
            if (payload.IsSingleSegment)
            {
                output.Write(payload.FirstSpan);
            }
            else
            {
                foreach (var segment in payload)
                    output.Write(segment.Span);
            }
        }

        [ThreadStatic]
        private static Encoder s_PerThreadEncoder;
        internal static Encoder GetPerThreadEncoder()
        {
            var encoder = s_PerThreadEncoder;
            if (encoder == null)
            {
                s_PerThreadEncoder = encoder = UTF8.GetEncoder();
            }
            else
            {
                encoder.Reset();
            }
            return encoder;
        }

        private static void WriteUtf8(IBufferWriter<byte> output, ReadOnlySpan<char> payload)
        {
            var encoder = GetPerThreadEncoder();
            bool final = false;
            while (true)
            {
                var span = output.GetSpan(5); // get *some* memory - at least enough for 1 character (but hopefully lots more)
                encoder.Convert(payload, span, final, out var charsUsed, out var bytesUsed, out var completed);
                output.Advance(bytesUsed);

                payload = payload.Slice(charsUsed);

                if (payload.IsEmpty)
                {
                    if (completed) break; // fine
                    if (final) throw new InvalidOperationException("String encode failed to complete");
                    final = true; // flush the encoder to one more span, then exit
                }
            }
        }

        private static ReadOnlySpan<byte> UnitAggregateBlobTemplate =>
            new byte[] { (byte)'\0', (byte)'1', (byte)'\r', (byte)'\n', (byte)'\0', (byte)'\0', (byte)'\r', (byte)'\n' };

        private static ReadOnlySpan<byte> UnitAggregateNullBlobTemplate =>
            new byte[] { (byte)'\0', (byte)'1', (byte)'\r', (byte)'\n', (byte)'\0', (byte)'-', (byte)'1', (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> Resp3Null =>
            new byte[] { (byte)'_', (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> Resp2NullTemplate =>
            new byte[] { (byte)'\0', (byte)'-', (byte)'1', (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> NewLine =>
            new byte[] { (byte)'\r', (byte)'\n' };

        private static ReadOnlySpan<byte> EmptyBlobTemplate =>
            new byte[] { (byte)'\0', (byte)'0', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

        private static ReadOnlySpan<byte> EmptySimpleTemplate =>
            new byte[] { (byte)'\0', (byte)'\r', (byte)'\n' };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsBlob(RespType type)
        {
            switch (type)
            {
                case RespType.BlobError:
                case RespType.BlobString:
                case RespType.VerbatimString:
                    return true;
                default:
                    return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsAggregate(RespType type)
        {
            switch (type)
            {
                case RespType.Array:
                case RespType.Attribute:
                case RespType.Map:
                case RespType.Set:
                case RespType.Push:
                    return true;
                default:
                    return false;
            }
        }
        private void WriteUnitAggregateInlinedBlob(IBufferWriter<byte> output)
        {
            // {type}1\r\n
            // {sub type}{length}\r\n
            // {payload}\r\n
            // except null, which is
            // {type}1\r\n
            // {sub type}-1\r\n
            var span = output.GetSpan(10 + State.InlineSize);
            UnitAggregateBlobTemplate.CopyTo(span);
            span[0] = GetPrefix(_state.Type);
            span[4] = GetPrefix(_state.SubType);
            int len = _state.PayloadLength;
            span[5] = (byte)(len + (byte)'0');
            _state.AsSpan().CopyTo(span.Slice(8));
            span[8 + len] = (byte)'\r';
            span[9 + len] = (byte)'\n';
            output.Advance(10 + len);
        }

        static void WriteEmpty(IBufferWriter<byte> output, RespType type)
        {
            // {type}0\r\n\r\n
            // or
            // {type}\r\n
            var source = IsBlob(type) ? EmptyBlobTemplate : EmptySimpleTemplate;
            var span = output.GetSpan(source.Length);
            source.CopyTo(span);
            span[0] = GetPrefix(type);
            output.Advance(source.Length);
        }
        static void WriteNull(IBufferWriter<byte> output, RespType type)
        {
            switch (type)
            {
                case RespType.Null:
                    output.Write(Resp3Null);
                    break;
                default:
                    // {type}-1\r\n
                    // (then the payload)
                    var span = output.GetSpan(5);
                    Resp2NullTemplate.CopyTo(span);
                    span[0] = GetPrefix(type);
                    output.Advance(5);
                    break;
            }

        }

        private static void WriteUnitAggregate(IBufferWriter<byte> output, RespType aggregateType, in RespValue value)
        {
            // {type}1\r\n
            // (then the payload)
            var span = output.GetSpan(4);
            span[0] = GetPrefix(aggregateType);
            span[1] = (byte)'1';
            span[2] = (byte)'\r';
            span[3] = (byte)'\n';
            output.Advance(4);
            value.Write(output);
        }

        public static bool TryParse(ReadOnlySequence<byte> input, out RespValue value, out SequencePosition end)
        {
            var reader = new SequenceReader<byte>(input);
            if (TryParse(ref reader, out value))
            {
                end = reader.Position;
                return true;
            }
            end = default;
            value = default;
            return false;
        }

        private static bool TryParse(ref SequenceReader<byte> input, out RespValue value)
        {
            if (input.TryRead(out var prefix))
            {
                var type = ParseType(prefix);
                switch (type)
                {
                    case RespType.BlobError:
                    case RespType.BlobString:
                    case RespType.VerbatimString:
                        return TryParseBlob(ref input, type, out value);
                    case RespType.Double:
                    case RespType.Boolean:
                    case RespType.Null:
                    case RespType.SimpleString:
                    case RespType.SimpleError:
                    case RespType.Number:
                    case RespType.BigNumber:
                        return TryParseLineTerminated(ref input, type, out value);
                    case RespType.Array:
                    case RespType.Set:
                    case RespType.Push:
                        return TryParseAggregate(ref input, type, out value, 1);
                    case RespType.Map:
                    case RespType.Attribute:
                        return TryParseAggregate(ref input, type, out value, 2);
                    default:
                        ThrowHelper.RespTypeNotImplemented(type);
                        break;
                }
            }
            value = default;
            return false;
        }

        static bool TryReadToEndOfLine(ref SequenceReader<byte> reader, out ReadOnlySequence<byte> payload)
        {
            if (reader.TryReadTo(out payload, (byte)'\r'))
            {
                if (!reader.TryRead(out var n)) return false;
                if (n == '\n') return true;
                ThrowHelper.ExpectedNewLine(n);
            }
            return false;
        }

        private static bool TryParseLineTerminated(ref SequenceReader<byte> input, RespType type, out RespValue message)
        {
            if (TryReadToEndOfLine(ref input, out var payload))
            {
                message = Create(type, payload);
                return true;
            }
            message = default;
            return false;
        }

        private static bool TryParseAggregate(ref SequenceReader<byte> input, RespType type, out RespValue message, int multiplier)
        {
            if (TryReadLength(ref input, out var length))
            {
                if (length == -1)
                {
                    message = new RespValue(new State(type));
                    return true;
                }
                if (length == 0)
                {
                    message = new RespValue(
                        new State(type, StorageKind.Empty, 0, 0));
                    return true;
                }
                length *= multiplier;
                if (length == 1)
                {
                    if (TryParse(ref input, out var unary))
                    {
                        if (unary._state.IsInlined && unary._state.SubType == RespType.Unknown)
                        {
                            message = new RespValue(unary._state.Wrap(type));
                        }
                        else
                        {
                            message = new RespValue(new State(type,
                                StorageKind.ArraySegmentRespValue, 0, 1),
                                new[] { unary });
                        }
                        return true;
                    }
                }
                var arr = new RespValue[length];
                message = new RespValue(
                    new State(type, StorageKind.ArraySegmentRespValue, 0, length), arr);
                for (int i = 0; i < length; i++)
                {
                    if (!TryParse(ref input, out arr[i])) return false;
                }
                return true;
            }
            message = default;
            return false;
        }

        private static bool TryReadLength(ref SequenceReader<byte> input, out int length)
        {
            if (TryReadToEndOfLine(ref input, out var payload))
            {
                int bytes;
                if (payload.IsSingleSegment)
                {
                    if (!Utf8Parser.TryParse(payload.First.Span, out length, out bytes)) ThrowHelper.Format();
                }
                else if (payload.Length <= 20)
                {
                    Span<byte> local = stackalloc byte[20];
                    payload.CopyTo(local);
                    if (!Utf8Parser.TryParse(local, out length, out bytes)) ThrowHelper.Format();
                }
                else
                {
                    ThrowHelper.Format();
                    length = bytes = 0;
                }
                if (bytes != payload.Length) ThrowHelper.Format();
                return true;
            }
            length = default;
            return false;
        }

        private static bool TryParseBlob(ref SequenceReader<byte> input, RespType type, out RespValue message)
        {
            if (TryReadLength(ref input, out var length))
            {
                switch(length)
                {
                    case -1:
                        if (TryAssertCRLF(ref input))
                        {
                            message = new RespValue(new State(type));
                            return true;
                        }
                        break;
                    case 0:
                        if (TryAssertCRLF(ref input))
                        {
                            message = new RespValue(new State(type, StorageKind.Empty, 0, 0));
                            return true;
                        }
                        break;
                    default:
                        if (input.Remaining >= length + 2)
                        {
                            if (length <= State.InlineSize)
                            {
                                var state = new State((byte)length, type);
                                if (!input.TryCopyTo(state.AsWritableSpan())) ThrowHelper.Argument(nameof(length));
                                message = new RespValue(state);
                            }
                            else
                            {
                                var arr = new byte[length];
                                message = new RespValue(
                                    new State(type, StorageKind.ArraySegmentByte, 0, length), arr);
                                if (!input.TryCopyTo(arr)) return false;
                            }

                            input.Advance(length); // note we already checked length
                            if (TryAssertCRLF(ref input)) return true;
                        }
                        break;
                }
            }
            message = default;
            return false;
        }

        static bool TryAssertCRLF(ref SequenceReader<byte> input)
        {
            if (input.TryRead(out var b))
            {
                if (b != (byte)'\r') ThrowHelper.ExpectedNewLine(b);
                if (input.TryRead(out b))
                {
                    if (b != (byte)'\n') ThrowHelper.ExpectedNewLine(b);
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetPrefix(RespType type) => (byte)type;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static RespType ParseType(byte value) => (RespType)value;

        public static RespValue CreateAggregate(RespType type, params RespValue[] values)
        {
            if (!IsAggregate(type)) ThrowHelper.Argument(nameof(type));
            if (values == null) return new RespValue(new State(type));
            return new RespValue(new State(type, StorageKind.ArraySegmentRespValue, 0, values.Length), values);
        }

        public static RespValue Create(RespType type, string value)
        {
            if (IsAggregate(type)) ThrowHelper.Argument(nameof(type));
            if (value == null) return new RespValue(new State(type));
            int len;
            if (value.Length <= State.InlineSize && (len = UTF8.GetByteCount(value)) <= State.InlineSize)
            {
                var state = new State((byte)len, type);
                UTF8.GetBytes(value.AsSpan(), state.AsWritableSpan());
                return new RespValue(state);
            }
            return new RespValue(new State(type, StorageKind.StringSegment, 0, value.Length), value);
        }

        public static RespValue Create(RespType type, long value)
        {
            if (IsAggregate(type)) ThrowHelper.Argument(nameof(type));
            return new RespValue(new State(type, value));
        }

        public static RespValue Create(RespType type, double value)
        {
            if (IsAggregate(type)) ThrowHelper.Argument(nameof(type));
            return new RespValue(new State(type, value));
        }
    }
}