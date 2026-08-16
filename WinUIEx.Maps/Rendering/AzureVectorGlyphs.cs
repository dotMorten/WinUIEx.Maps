using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Lazily acquires the bounded Mapbox glyph ranges referenced by one Azure vector style.
/// </summary>
internal sealed class AzureGlyphProvider : IVectorGlyphProvider
{
    private const int MaximumGlyphRangeBytes = 2 * 1024 * 1024;
    private const int MaximumCachedRanges = 512;
    private readonly object _sync = new();
    private readonly MapStyle _style;
    private readonly string _styleSlug;
    private readonly string _token;
    private readonly Dictionary<AzureGlyphRangeKey, Task<AzureGlyphRange>> _ranges = [];

    internal AzureGlyphProvider(MapStyle style, string token)
    {
        _style = style;
        _styleSlug = AzureTileAcquisitionSession.GetAzureStyleName(style);
        _token = token;
    }

    internal async Task<AzureGlyphRange> GetRangeAsync(
        string fontStack,
        int rangeStart,
        CancellationToken cancellationToken)
    {
        AzureGlyphRangeKey key = new(fontStack, rangeStart);
        Task<AzureGlyphRange> task;
        lock (_sync)
        {
            if (_ranges.TryGetValue(key, out task!))
            {
                task = _ranges[key];
            }
            else
            {
                if (_ranges.Count >= MaximumCachedRanges)
                {
                    throw new InvalidDataException(
                        "The Azure vector glyph range cache exceeds its supported limit.");
                }
                task = LoadRangeAsync(key, CancellationToken.None);
                _ranges.Add(key, task);
            }
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted && !task.IsCompletedSuccessfully)
            {
                lock (_sync)
                {
                    if (_ranges.TryGetValue(
                            key,
                            out Task<AzureGlyphRange>? current) &&
                        ReferenceEquals(current, task))
                    {
                        _ranges.Remove(key);
                    }
                }
            }
        }
    }

    Task<AzureGlyphRange> IVectorGlyphProvider.GetRangeAsync(
        string fontStack,
        int rangeStart,
        CancellationToken cancellationToken) =>
        GetRangeAsync(fontStack, rangeStart, cancellationToken);

    private async Task<AzureGlyphRange> LoadRangeAsync(
        AzureGlyphRangeKey key,
        CancellationToken cancellationToken)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string escapedFont = Uri.EscapeDataString(key.FontStack);
        string path =
            $"styling/glyphs/{escapedFont}/{key.RangeStart}-{key.RangeStart + 255}.pbf?styleVersion=2023-01-01&api-version=2.0";
        byte[] encoded;
        try
        {
            encoded = await AzureTileAcquisitionSession.GetStyleAssetAsync(
                    path,
                    _token,
                    "application/x-protobuf",
                    MaximumGlyphRangeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AzureMapsRequestException exception) when (
            exception.StatusCode is
                System.Net.HttpStatusCode.BadRequest or
                System.Net.HttpStatusCode.NotFound)
        {
            MapControlEventSource.Log.VectorGlyphRangeUnavailable(
                (int)_style,
                key.RangeStart,
                (int)exception.StatusCode,
                exception.GetType().Name,
                System.Diagnostics.Stopwatch.GetElapsedTime(started)
                    .TotalMilliseconds);
            return new AzureGlyphRange(
                key.FontStack,
                key.RangeStart,
                new Dictionary<int, AzureGlyph>());
        }
        catch (AzureMapsRequestException exception)
        {
            exception.DiagnosticExceptionType =
                "AzureMapsGlyphRangeRequestException";
            throw;
        }
        AzureGlyphRange range = AzureGlyphRangeDecoder.Decode(
            encoded,
            key.FontStack,
            key.RangeStart,
            cancellationToken);
        MapControlEventSource.Log.VectorGlyphRangeLoaded(
            (int)_style,
            range.Glyphs.Count,
            encoded.Length,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return range;
    }
}

internal readonly record struct AzureGlyphRangeKey(string FontStack, int RangeStart);

internal sealed record AzureGlyphRange(
    string FontStack,
    int RangeStart,
    IReadOnlyDictionary<int, AzureGlyph> Glyphs);

internal sealed record AzureGlyph(
    int Id,
    byte[] Bitmap,
    uint Width,
    uint Height,
    int Left,
    int Top,
    uint Advance)
{
    internal const uint SdfBuffer = 3;

    internal uint TextureWidth =>
        Width == 0 && Bitmap.Length == 0 ? 0 : Width + (SdfBuffer * 2);

    internal uint TextureHeight =>
        Height == 0 && Bitmap.Length == 0 ? 0 : Height + (SdfBuffer * 2);
}

/// <summary>
/// Decodes the Mapbox fontstack/glyph protobuf schema returned by Azure styling.
/// </summary>
internal static class AzureGlyphRangeDecoder
{
    private const int MaximumGlyphs = 256;
    private const int MaximumGlyphDimension = 128;
    private const int MaximumFontNameBytes = 1024;
    private const int MaximumRangeBytes = 64;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static AzureGlyphRange Decode(
        ReadOnlySpan<byte> encoded,
        string expectedFontStack,
        int expectedRangeStart,
        CancellationToken cancellationToken = default)
    {
        GlyphProtobufReader root = new(encoded);
        AzureGlyphRange? result = null;
        while (root.TryReadField(out int fieldNumber, out int wireType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fieldNumber == 1 && wireType == 2)
            {
                if (result is not null)
                {
                    throw new InvalidDataException(
                        "The Azure glyph response contains multiple font stacks.");
                }
                result = DecodeFontStack(
                    root.ReadLengthDelimited(),
                    expectedFontStack,
                    expectedRangeStart,
                    cancellationToken);
            }
            else
            {
                root.SkipField(wireType);
            }
        }
        return result ?? throw new InvalidDataException(
            "The Azure glyph response contains no font stack.");
    }

    private static AzureGlyphRange DecodeFontStack(
        ReadOnlySpan<byte> encoded,
        string expectedFontStack,
        int expectedRangeStart,
        CancellationToken cancellationToken)
    {
        string? name = null;
        string? range = null;
        Dictionary<int, AzureGlyph> glyphs = [];
        GlyphProtobufReader reader = new(encoded);
        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (fieldNumber, wireType)
            {
                case (1, 2):
                    name = ReadString(
                        reader.ReadLengthDelimited(),
                        MaximumFontNameBytes,
                        "font name");
                    break;
                case (2, 2):
                    range = ReadString(
                        reader.ReadLengthDelimited(),
                        MaximumRangeBytes,
                        "range");
                    break;
                case (3, 2):
                    if (glyphs.Count >= MaximumGlyphs)
                    {
                        throw new InvalidDataException(
                            "The Azure glyph range contains too many glyphs.");
                    }
                    AzureGlyph glyph = DecodeGlyph(reader.ReadLengthDelimited());
                    if (!glyphs.TryAdd(glyph.Id, glyph))
                    {
                        throw new InvalidDataException(
                            "The Azure glyph range contains a duplicate glyph.");
                    }
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        string expectedRange =
            string.Create(
                CultureInfo.InvariantCulture,
                $"{expectedRangeStart}-{expectedRangeStart + 255}");
        if (string.IsNullOrWhiteSpace(name) ||
            !string.Equals(range, expectedRange, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Azure glyph response does not match the requested font range.");
        }
        return new AzureGlyphRange(expectedFontStack, expectedRangeStart, glyphs);
    }

    private static AzureGlyph DecodeGlyph(ReadOnlySpan<byte> encoded)
    {
        int? id = null;
        byte[] bitmap = [];
        uint? width = null;
        uint? height = null;
        int left = 0;
        int top = 0;
        uint advance = 0;
        GlyphProtobufReader reader = new(encoded);
        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber, wireType)
            {
                case (1, 0):
                    id = checked((int)reader.ReadVarint());
                    break;
                case (2, 2):
                    bitmap = reader.ReadLengthDelimited().ToArray();
                    break;
                case (3, 0):
                    width = checked((uint)reader.ReadVarint());
                    break;
                case (4, 0):
                    height = checked((uint)reader.ReadVarint());
                    break;
                case (5, 0):
                    left = DecodeZigZag32(reader.ReadVarint());
                    break;
                case (6, 0):
                    top = DecodeZigZag32(reader.ReadVarint());
                    break;
                case (7, 0):
                    advance = checked((uint)reader.ReadVarint());
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }
        if (id is null ||
            id < 0 ||
            width is null ||
            height is null ||
            width > MaximumGlyphDimension ||
            height > MaximumGlyphDimension ||
            bitmap.Length != (width == 0 && height == 0
                ? 0
                : checked((int)(
                    (width.Value + (AzureGlyph.SdfBuffer * 2)) *
                    (height.Value + (AzureGlyph.SdfBuffer * 2))))))
        {
            throw new InvalidDataException(
                "The Azure glyph response contains an invalid glyph.");
        }
        return new AzureGlyph(
            id.Value,
            bitmap,
            width.Value,
            height.Value,
            left,
            top,
            advance);
    }

    private static int DecodeZigZag32(ulong value)
    {
        long decoded =
            unchecked((long)(value >> 1)) ^ -unchecked((long)(value & 1));
        return checked((int)decoded);
    }

    private static string ReadString(
        ReadOnlySpan<byte> encoded,
        int maximumBytes,
        string description)
    {
        if (encoded.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The Azure glyph {description} exceeds its supported limit.");
        }
        try
        {
            return StrictUtf8.GetString(encoded);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The Azure glyph {description} is not valid UTF-8.",
                exception);
        }
    }

    private ref struct GlyphProtobufReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _offset;

        internal readonly bool End => _offset == _buffer.Length;

        internal bool TryReadField(out int fieldNumber, out int wireType)
        {
            if (End)
            {
                fieldNumber = 0;
                wireType = 0;
                return false;
            }
            ulong tag = ReadVarint();
            fieldNumber = checked((int)(tag >> 3));
            wireType = (int)(tag & 7);
            if (fieldNumber == 0)
            {
                throw new InvalidDataException(
                    "The Azure glyph response contains an invalid protobuf field.");
            }
            return true;
        }

        internal ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_offset >= _buffer.Length)
                {
                    throw new InvalidDataException(
                        "The Azure glyph response ended inside a varint.");
                }
                byte current = _buffer[_offset++];
                if (shift == 63 && current > 1)
                {
                    throw new InvalidDataException(
                        "The Azure glyph response contains an oversized varint.");
                }
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException(
                "The Azure glyph response contains an oversized varint.");
        }

        internal ReadOnlySpan<byte> ReadLengthDelimited()
        {
            ulong length = ReadVarint();
            if (length > int.MaxValue || length > (ulong)(_buffer.Length - _offset))
            {
                throw new InvalidDataException(
                    "The Azure glyph response contains an invalid field length.");
            }
            ReadOnlySpan<byte> value = _buffer.Slice(_offset, (int)length);
            _offset += (int)length;
            return value;
        }

        internal void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ReadVarint();
                    break;
                case 1:
                    Skip(8);
                    break;
                case 2:
                    _ = ReadLengthDelimited();
                    break;
                case 5:
                    Skip(4);
                    break;
                default:
                    throw new InvalidDataException(
                        "The Azure glyph response uses an unsupported protobuf wire type.");
            }
        }

        private void Skip(int count)
        {
            if (count > _buffer.Length - _offset)
            {
                throw new InvalidDataException(
                    "The Azure glyph response ended inside a fixed-width field.");
            }
            _offset += count;
        }
    }
}

internal interface IVectorGlyphProvider
{
    Task<AzureGlyphRange> GetRangeAsync(
        string fontStack,
        int rangeStart,
        CancellationToken cancellationToken);
}

/// <summary>
/// Retains decoded glyph metrics and lazily generated grayscale GPU texture buffers.
/// </summary>
internal sealed class AzureGlyphAtlas
{
    private const int MaximumGlyphTextures = 16_384;
    private const long MaximumGlyphTextureBytes = 64 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly string _styleSlug;
    private readonly IVectorGlyphProvider? _provider;
    private readonly Dictionary<AzureGlyphRangeKey, AzureGlyphRange> _ranges = [];
    private readonly Dictionary<AzureGlyphKey, VectorSpriteTextureData> _textures = [];
    private long _textureBytes;

    internal AzureGlyphAtlas(string styleSlug, IVectorGlyphProvider? provider)
    {
        _styleSlug = styleSlug;
        _provider = provider;
    }

    internal void AddRangeForTest(AzureGlyphRange range)
    {
        lock (_sync)
        {
            _ranges[new AzureGlyphRangeKey(range.FontStack, range.RangeStart)] = range;
        }
    }

    internal async Task PrepareAsync(
        IReadOnlyCollection<AzureGlyphKey> keys,
        CancellationToken cancellationToken)
    {
        AzureGlyphRangeKey[] missing;
        lock (_sync)
        {
            missing =
            [
                .. keys
                    .Select(key => new AzureGlyphRangeKey(
                        key.FontStack,
                        key.CodePoint & ~255))
                    .Where(key => !_ranges.ContainsKey(key))
                    .Distinct(),
            ];
        }
        if (missing.Length != 0 && _provider is null)
        {
            throw new InvalidOperationException(
                "No vector glyph provider is available for the requested font range.");
        }
        AzureGlyphRange[] loaded = await Task.WhenAll(
            missing.Select(key => _provider!.GetRangeAsync(
                key.FontStack,
                key.RangeStart,
                cancellationToken))).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (AzureGlyphRange range in loaded)
            {
                _ranges.TryAdd(
                    new AzureGlyphRangeKey(range.FontStack, range.RangeStart),
                    range);
            }
        }
    }

    internal bool TryGetOrCreateTexture(
        AzureGlyphKey key,
        out AzureGlyph glyph,
        out VectorSpriteTextureData? texture)
    {
        lock (_sync)
        {
            glyph = null!;
            AzureGlyphRangeKey rangeKey =
                new(key.FontStack, key.CodePoint & ~255);
            if (!_ranges.TryGetValue(rangeKey, out AzureGlyphRange? range) ||
                !range.Glyphs.TryGetValue(
                    key.CodePoint,
                    out AzureGlyph? foundGlyph))
            {
                texture = null;
                return false;
            }
            glyph = foundGlyph;
            if (_textures.TryGetValue(key, out texture))
            {
                return true;
            }
            if (glyph.TextureWidth == 0 || glyph.TextureHeight == 0)
            {
                texture = null;
                return true;
            }
            long byteCount = checked(
                (long)glyph.TextureWidth * glyph.TextureHeight * 4);
            if (_textures.Count >= MaximumGlyphTextures ||
                byteCount > MaximumGlyphTextureBytes - _textureBytes)
            {
                throw new InvalidDataException(
                    "The Azure glyph texture cache exceeds its supported limit.");
            }
            byte[] pixels = new byte[checked((int)byteCount)];
            for (int index = 0; index < glyph.Bitmap.Length; index++)
            {
                byte distance = glyph.Bitmap[index];
                int destination = index * 4;
                pixels[destination] = distance;
                pixels[destination + 1] = distance;
                pixels[destination + 2] = distance;
                pixels[destination + 3] = byte.MaxValue;
            }
            long textureId = CreateTextureId(
                _styleSlug,
                key.FontStack,
                key.CodePoint);
            texture = new VectorSpriteTextureData(
                textureId,
                pixels,
                glyph.TextureWidth,
                glyph.TextureHeight);
            _textures.Add(key, texture);
            _textureBytes += byteCount;
            return true;
        }
    }

    internal static long CreateTextureId(
        string styleSlug,
        string fontStack,
        int codePoint)
    {
        byte[] identity = Encoding.UTF8.GetBytes(
            $"glyph\0{styleSlug}\0{fontStack}\0{codePoint.ToString(CultureInfo.InvariantCulture)}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(identity, hash);
        return BinaryPrimitives.ReadInt64LittleEndian(hash) | long.MinValue;
    }
}

internal readonly record struct AzureGlyphKey(string FontStack, int CodePoint);
