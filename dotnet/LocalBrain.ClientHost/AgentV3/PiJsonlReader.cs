using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LocalBrain.ClientHost.AgentV3
{
    internal sealed class PiJsonlReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly Stream _stream;
        private readonly int _maxRecordBytes;
        private readonly byte[] _buffer = new byte[4096];
        private int _bufferOffset;
        private int _bufferCount;

        public PiJsonlReader(Stream stream, int maxRecordBytes)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (maxRecordBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));
            }

            _maxRecordBytes = maxRecordBytes;
        }

        public async Task<JsonElement?> ReadAsync(CancellationToken token)
        {
            using (var record = new MemoryStream())
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (_bufferOffset == _bufferCount)
                    {
                        _bufferCount = await _stream.ReadAsync(_buffer, 0, _buffer.Length, token).ConfigureAwait(false);
                        _bufferOffset = 0;
                        if (_bufferCount == 0)
                        {
                            if (record.Length == 0)
                            {
                                return null;
                            }

                            throw new InvalidDataException("The final JSON Lines record is incomplete.");
                        }
                    }

                    byte value = _buffer[_bufferOffset++];
                    if (value == (byte)'\n')
                    {
                        int length = checked((int)record.Length);
                        byte[] bytes = record.GetBuffer();
                        if (length > 0 && bytes[length - 1] == (byte)'\r')
                        {
                            length--;
                        }

                        if (length == 0)
                        {
                            throw new InvalidDataException("JSON Lines records must not be empty.");
                        }

                        try
                        {
                            string json = StrictUtf8.GetString(bytes, 0, length);
                            using (JsonDocument document = JsonDocument.Parse(json))
                            {
                                if (document.RootElement.ValueKind != JsonValueKind.Object)
                                {
                                    throw new InvalidDataException("A JSON Lines record must be an object.");
                                }

                                return document.RootElement.Clone();
                            }
                        }
                        catch (DecoderFallbackException ex)
                        {
                            throw new InvalidDataException("A JSON Lines record contains invalid UTF-8.", ex);
                        }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException("A JSON Lines record contains invalid JSON.", ex);
                        }
                    }

                    // Permit one extra byte only when it could be the CR in CRLF.
                    if (record.Length > _maxRecordBytes ||
                        (record.Length == _maxRecordBytes && value != (byte)'\r'))
                    {
                        throw new InvalidDataException("A JSON Lines record exceeds the byte limit.");
                    }

                    record.WriteByte(value);
                }
            }
        }
    }
}
