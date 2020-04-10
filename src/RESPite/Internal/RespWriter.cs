﻿using System;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Respite.Internal
{

    internal ref struct RespWriter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Complete()
        {
            Flush(-1);
            return TotalBytesWritten;
        }

        public long TotalBytesWritten { get; private set; }

        public readonly RespVersion Version;
        private readonly IBufferWriter<byte> _writer;
        private int _writtenBytesThisSpan;
        private Span<byte> _currentSpan;
        public RespWriter(RespVersion version, IBufferWriter<byte> writer)
        {
            Version = version;
            _writer = writer;
            _writtenBytesThisSpan = -1;
            _currentSpan = default;
            TotalBytesWritten = 0;
        }
        void Flush(int sizeHint = 512)
        {
            var written = _writtenBytesThisSpan;
             _writtenBytesThisSpan = -1;
            if (written >= 0)
            {
                _writer.Advance(written);
                TotalBytesWritten += written;
                _currentSpan = default;
            }
            if (sizeHint >= 0)
            {
                _currentSpan = _writer.GetSpan(sizeHint);
                _writtenBytesThisSpan = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Commit(int count)
        {
            if (count < 0) ThrowHelper.ArgumentOutOfRange(nameof(count));
            _currentSpan = _currentSpan.Slice(count);
            _writtenBytesThisSpan += count;
        }
        public void Write(RespType type)
        {
            if (_currentSpan.IsEmpty) Flush();
            _currentSpan[0] = (byte)type;
            Commit(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(in ReadOnlySequence<byte> value)
        {
            if (value.IsSingleSegment) Write(value.First.Span);
            else SlowWrite(in value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(in ReadOnlySequence<byte> value)
        {
            foreach (var segment in value)
                Write(segment.Span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(in ReadOnlySequence<char> value)
        {
            if (value.IsSingleSegment) Write(value.First.Span);
            else SlowWrite(in value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(in ReadOnlySequence<char> value)
        {
            foreach (var segment in value)
                Write(segment.Span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ReadOnlySpan<byte> value)
        {
            if (value.TryCopyTo(_currentSpan)) Commit(value.Length);
            else SlowWrite(value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(ReadOnlySpan<byte> value)
        {
            while (true)
            {
                if (value.TryCopyTo(_currentSpan))
                {   // it fits
                    Commit(value.Length);
                    return;
                }
                // copy whatever is available
                int bytes = _currentSpan.Length;
                value.Slice(0, bytes).CopyTo(_currentSpan);
                value = value.Slice(bytes);
                Commit(bytes);
                Flush(); // we expect more, note
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Span<byte> Ensure(int sizeHint)
            => _currentSpan.Length >= sizeHint ? _currentSpan : SlowEnsure(sizeHint);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Span<byte> SlowEnsure(int sizeHint)
        {
            Flush(sizeHint);
            return _currentSpan;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ReadOnlySpan<char> value)
        {
            if (Encoding.UTF8.GetMaxByteCount(value.Length) <= _currentSpan.Length)
                Commit(Encoding.UTF8.GetBytes(value, _currentSpan));
            else SlowWrite(value);
        }

        [ThreadStatic]
        private static Encoder? s_PerThreadEncoder;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(ReadOnlySpan<char> value)
        {
            var encoder = s_PerThreadEncoder ??= Encoding.UTF8.GetEncoder();
            encoder.Reset();

            bool final = false;
            while (true)
            {
                encoder.Convert(value, _currentSpan, final, out var charsUsed, out var bytesUsed, out var completed);
                Commit(bytesUsed);

                value = value.Slice(charsUsed);
                if (!completed) Flush();
                if (value.IsEmpty)
                {
                    if (completed) break; // fine
                    if (final) ThrowHelper.Invalid("String encode failed to complete");
                    final = true; // flush the encoder to one more span, then exit
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLine() => Write(NewLine);
        private static ReadOnlySpan<byte> NewLine => new byte[] { (byte)'\r', (byte)'\n' };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint value)
        {
            if (Utf8Formatter.TryFormat(value, _currentSpan, out var bytes)) Commit(bytes);
            else SlowWrite(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Write(uint value, Span<byte> destination)
        {
            if (!Utf8Formatter.TryFormat(value, destination, out var bytes))
                ThrowHelper.Format();
            return bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(uint value)
        {
            Flush();
            if (!Utf8Formatter.TryFormat(value, _currentSpan, out var bytes))
                ThrowHelper.Format();
            Commit(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(long value)
        {
            if (Utf8Formatter.TryFormat(value, _currentSpan, out var bytes)) Commit(bytes);
            else SlowWrite(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Write(long value, Span<byte> destination)
        {
            if (!Utf8Formatter.TryFormat(value, destination, out var bytes))
                ThrowHelper.Format();
            return bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(long value)
        {
            Flush();
            if (!Utf8Formatter.TryFormat(value, _currentSpan, out var bytes))
                ThrowHelper.Format();
            Commit(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(double value)
        {
            if (Utf8Formatter.TryFormat(value, _currentSpan, out var bytes, s_DoubleFormat)) Commit(bytes);
            else SlowWrite(value);
        }

        private static readonly StandardFormat s_DoubleFormat
            = new StandardFormat('G', 17);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Write(double value, Span<byte> destination)
        {
            if (!Utf8Formatter.TryFormat(value, destination, out var bytes, s_DoubleFormat))
                ThrowHelper.Format();
            return bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWrite(double value)
        {
            Flush();
            if (!Utf8Formatter.TryFormat(value, _currentSpan, out var bytes, s_DoubleFormat))
                ThrowHelper.Format();
            Commit(bytes);
        }

        internal RespType Downgrade(RespType type)
        {
            switch(type)
            {
                case RespType.Boolean:
                case RespType.Double:
                case RespType.BigNumber:
                    if (Version < RespVersion.RESP3) return RespType.SimpleString;
                    break;
                case RespType.VerbatimString:
                    if (Version < RespVersion.RESP3) return RespType.BlobString;
                    break;
                case RespType.Push:
                case RespType.Map:
                case RespType.Set:
                    if (Version < RespVersion.RESP3) return RespType.Array;
                    break;
            }
            return type;
        }
    }
}
