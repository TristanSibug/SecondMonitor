using System;
using System.IO;
using System.Text;

// AVI is used only on the local ffmpeg pipe: each 00dc chunk has a complete
// H.264 access unit and its length. The tablet still receives raw H.264 frames.
// Unlike scanning for the next AUD, this can deliver the last frame immediately.
sealed class AviFrameReader
{
    const uint MaxChunk = 32 * 1024 * 1024;
    readonly Stream stream;
    readonly byte[] header = new byte[8];
    readonly byte[] discard = new byte[8192];
    bool inMovie;
    public AviFrameReader(Stream stream)
    {
        this.stream = stream;
        stream.ReadExactly(header);
        if (Tag(header) != "RIFF") throw new InvalidDataException("Expected ffmpeg AVI output.");
        byte[] type = new byte[4];
        stream.ReadExactly(type);
        if (Tag(type) != "AVI ") throw new InvalidDataException("Expected an AVI video stream.");
    }
    static string Tag(byte[] bytes) { return Encoding.ASCII.GetString(bytes, 0, 4); }
    static uint Size(byte[] bytes)
    {
        return (uint)bytes[4] | (uint)bytes[5] << 8 | (uint)bytes[6] << 16 | (uint)bytes[7] << 24;
    }
    void Skip(uint count)
    {
        if (count > MaxChunk) throw new InvalidDataException("Oversized AVI metadata chunk.");
        while (count > 0)
        {
            int n = (int)Math.Min((uint)discard.Length, count);
            stream.ReadExactly(discard, 0, n); count -= (uint)n;
        }
    }
    public byte[] ReadFrame()
    {
        while (true)
        {
            int first = stream.ReadByte();
            if (first < 0) return null;
            header[0] = (byte)first;
            stream.ReadExactly(header, 1, 7);
            string tag = Tag(header);
            uint size = Size(header);
            if (tag == "RIFF" || tag == "LIST")
            {
                byte[] type = new byte[4]; stream.ReadExactly(type);
                string kind = Tag(type);
                if (tag == "RIFF")
                {
                    if (kind != "AVIX") throw new InvalidDataException("Unexpected AVI segment.");
                    inMovie = false;
                }
                else if (kind == "movi") inMovie = true;
                else if (kind != "rec ")
                {
                    if (size < 4 || size > MaxChunk) throw new InvalidDataException("Invalid AVI list.");
                    Skip(size - 4 + (size & 1));
                }
                continue;
            }
            if (size > MaxChunk) throw new InvalidDataException("Oversized AVI frame.");
            if (inMovie && tag == "00dc" && size > 0)
            {
                byte[] frame = new byte[(int)size]; stream.ReadExactly(frame);
                if ((size & 1) != 0) Skip(1);
                return frame;
            }
            Skip(size + (size & 1));
        }
    }
}
