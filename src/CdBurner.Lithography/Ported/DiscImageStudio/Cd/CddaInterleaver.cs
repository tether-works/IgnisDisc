// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2008-2022 arduinocelentano
// Copyright (c) 2026 Disc Image Studio contributors
// Adapted from CDImage's converter.cpp / converter.h:
// https://github.com/arduinocelentano/cdimage/tree/de5ca19f403b86934cfedb6793ca75f6716080c0/src
// C# port and subsequent stream, byte-order and shared-table changes: 2026.
// Attribution restored 2026-10-25; see Git history and THIRD-PARTY-NOTICES.txt.
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Cd/CddaInterleaver.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Cd/CddaInterleaver.cs
// IgnisDisc changes: namespace; the delay line is primed with real lead-in bytes (no zero placeholders at the
// start of a track) and Flush checks that a full lead-out pushed every byte of the drawn stretch out (upstream's
// docs/PHYSICAL_MODEL_AUDIT.md, finding four). See THIRD-PARTY-NOTICES.md.

using System.IO;

namespace CdBurner.Lithography.Cd;

/// <summary>
/// Pre-compensates the drive's CIRC delays (ECMA-130 annex C, figure C.4) so that a logical byte stream lands in
/// order on the disc. A byte at logical index i leaves at stream index i + FileOffsetFor(i % 24), 3 to 108 frames
/// later, so a finite stretch has two edges: the first bytes out need logical bytes from before the stretch, and
/// the last bytes of the stretch need output after it. <see cref="Prime"/> supplies the first (lead-in bytes whose
/// own output is not written), and the caller supplies the second by ending the stream with
/// <see cref="ContextBytes"/> lead-out bytes. Then every output byte is a real input byte and every byte of the
/// stretch between the two is emitted once.
/// </summary>
internal sealed class CddaInterleaver(CdAudioByteOrder audioByteOrder)
{
    /// <summary>Lead-in and lead-out length: the longest file offset (108 frames, ECMA-130 C.9).</summary>
    internal static int ContextBytes { get; } = CddaInterleaveTable.MaximumFileOffset;


    private readonly byte[] _interleaveSequence =
        new byte[CddaInterleaveTable.FramesPerCycle * CddaInterleaveTable.BytesPerFrame];
    private readonly byte[] _sector = new byte[CdDiscParameters.BytesPerSector];
    private int _head = CddaInterleaveTable.FramesPerCycle - 1;
    private int _inputPosition;
    private int _sectorPosition;
    private long _suppressBytes;
    private long _addedAfterPrime;
    private bool _primed;

    /// <summary>
    /// Fills the delay line with <see cref="ContextBytes"/> lead-in bytes before the stretch. Their own output
    /// would lie before the start of the stream, so it is not written; what the stream starts with is the
    /// stretch interleaved with these bytes instead of the zero placeholders of an empty delay line.
    /// </summary>
    internal void Prime(ReadOnlySpan<byte> leadIn)
    {
        if (_primed || _addedAfterPrime != 0)
            throw new InvalidOperationException("The interleaver can only be primed once, before any other input.");
        if (leadIn.Length != ContextBytes)
            throw new ArgumentException($"The lead-in must be exactly {ContextBytes} bytes.", nameof(leadIn));
        _primed = true;
        _suppressBytes = leadIn.Length;
        Add(leadIn, Stream.Null);
        _addedAfterPrime = 0;
    }

    internal void Add(byte value, Stream output)
    {
        _addedAfterPrime++;
        _interleaveSequence[MapIndex(CddaInterleaveTable.DelayFor(_inputPosition++))] = value;
        if (_inputPosition < CddaInterleaveTable.BytesPerFrame)
        {
            return;
        }

        CompleteFrame(output);
    }

    internal void Add(ReadOnlySpan<byte> values, Stream output)
    {
        _addedAfterPrime += values.Length;
        while (!values.IsEmpty)
        {
            int count = Math.Min(values.Length, CddaInterleaveTable.BytesPerFrame - _inputPosition);
            for (int index = 0; index < count; index++)
                _interleaveSequence[MapIndex(CddaInterleaveTable.DelayFor(_inputPosition + index))] = values[index];
            _inputPosition += count;
            values = values[count..];
            if (_inputPosition == CddaInterleaveTable.BytesPerFrame) CompleteFrame(output);
        }
    }

    private void CompleteFrame(Stream output)
    {
        _inputPosition = 0;
        _head++;
        if (_head >= CddaInterleaveTable.FramesPerCycle)
        {
            _head = 0;
        }

        // Both the ring and an audio sector contain whole 24-byte frames. The
        // completed frame is contiguous even when the head wrapped to zero.
        if (_suppressBytes > 0)
        {
            _suppressBytes -= CddaInterleaveTable.BytesPerFrame;
            return;
        }

        _interleaveSequence.AsSpan(_head * CddaInterleaveTable.BytesPerFrame,
            CddaInterleaveTable.BytesPerFrame).CopyTo(_sector.AsSpan(_sectorPosition));
        _sectorPosition += CddaInterleaveTable.BytesPerFrame;
        if (_sectorPosition == _sector.Length)
        {
            CdAudioSamples.WriteSector(output, _sector, audioByteOrder);
            _sectorPosition = 0;
        }
    }

    /// <summary>
    /// Ends the stream. The last <see cref="ContextBytes"/> bytes added are the lead-out: every byte added before
    /// them has been written by now. The stream must have been primed, so it holds no zero placeholders.
    /// </summary>
    internal void Flush(Stream output)
    {
        if (!_primed)
            throw new InvalidOperationException("Prime the interleaver with its lead-in before ending the stream.");
        if (_addedAfterPrime < ContextBytes)
            throw new InvalidOperationException(
                $"The stream must end with a lead-out of at least {ContextBytes} bytes to drain the delay line.");
        if (_sectorPosition == 0)
        {
            return;
        }

        Array.Clear(_sector, _sectorPosition, _sector.Length - _sectorPosition);
        CdAudioSamples.WriteSector(output, _sector, audioByteOrder);
        _sectorPosition = 0;
    }

    private int MapIndex(int position)
    {
        int value = (_head * CddaInterleaveTable.BytesPerFrame) + position;
        int modulus = _interleaveSequence.Length;
        if (value >= modulus)
        {
            return value - modulus;
        }

        return value < 0 ? value + modulus : value;
    }
}
