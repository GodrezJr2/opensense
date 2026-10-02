using System.Text;
using OpenSense.Core.Hardware;

namespace OpenSense.Core.Tests;

public sealed class AcerServiceTransportTests
{
    /// <summary>A socket that hands over its reply in pieces, then goes quiet (a read times out) or ends.</summary>
    private sealed class PiecesStream(bool endsWithTimeout, params byte[][] pieces) : Stream
    {
        private int _next;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_next >= pieces.Length)
                return endsWithTimeout ? throw new IOException("timed out") : 0;
            var piece = pieces[_next++];
            piece.CopyTo(buffer, offset);
            return piece.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reply_that_comes_in_pieces_is_read_whole(bool endsWithTimeout)
    {
        var json = "{\"data\":{\"zones\":[1,2,3,4],\"name\":\"keyboard\"},\"result\":0}";
        var cut = json.Length / 3;
        var stream = new PiecesStream(endsWithTimeout, Bytes(json[..cut]), Bytes(json[cut..(2 * cut)]), Bytes(json[(2 * cut)..]));

        var response = AcerServiceTransport.ReadResponse(stream, _ => { });

        Assert.Equal(json, Encoding.UTF8.GetString(response));
        Assert.Equal(0, AcerServiceTransport.ParseResponse(response, null)!["result"]!.GetValue<int>());
    }

    [Fact]
    public void The_wait_shortens_once_the_first_piece_is_in()
    {
        var timeouts = new List<int>();
        AcerServiceTransport.ReadResponse(new PiecesStream(true, Bytes("{}")), timeouts.Add);
        Assert.Equal(300, Assert.Single(timeouts));
    }

    [Fact]
    public void Nothing_at_all_is_an_empty_reply()
    {
        Assert.Empty(AcerServiceTransport.ReadResponse(new PiecesStream(false), _ => { }));
        Assert.Null(AcerServiceTransport.ParseResponse([], null));
    }

    [Fact]
    public void The_acer_header_is_skipped()
    {
        byte[] reply = [.. Bytes("ACER"), 20, 0, 0, 0, .. Bytes("{\"result\":0}")];
        Assert.Equal(0, AcerServiceTransport.ParseResponse(reply, null)!["result"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{\"data\":{\"zones\":[1,2,")] // cut short, as a single read of a long reply can be
    [InlineData("not json")]
    [InlineData("{\"a\":\"unterminated")]
    public void A_reply_that_is_not_complete_json_is_no_reply_rather_than_an_error(string text)
    {
        Assert.Null(AcerServiceTransport.ParseResponse(Bytes(text), null));
    }

    [Fact]
    public void A_keyed_reply_that_does_not_decrypt_is_no_reply()
    {
        var key = Bytes("0123456789abcdef0123456789abcdef");
        Assert.Null(AcerServiceTransport.ParseResponse(Bytes("this is not a multiple of sixteen"), key));
        Assert.Null(AcerServiceTransport.ParseResponse(new byte[32], key));
    }
}
