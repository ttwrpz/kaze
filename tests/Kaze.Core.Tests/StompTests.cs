using Kaze.Core.Cloud;

namespace Kaze.Core.Tests;

public class StompTests
{
    [Fact]
    public void Connect_frame_matches_known_working_shape()
    {
        var f = StompCodec.Connect("abc", TimeSpan.FromSeconds(10));
        Assert.Equal("CONNECT\naccept-version:1.1,1.2\nheart-beat:10000,10000\nAuthorization:Bearer abc\n\n\0", f);
    }

    [Fact]
    public void Subscribe_frame_uses_family_destination()
    {
        var f = StompCodec.Subscribe("sub-1", "42");
        Assert.Equal("SUBSCRIBE\nid:sub-1\ndestination:/notification/42/42\nack:auto\n\n\0", f);
    }

    [Fact]
    public void Reader_splits_frames_and_handles_partial_chunks()
    {
        var r = new StompFrameReader();
        Assert.Single(r.Feed("CONNECTED\nversion:1.2\nuser-name:x\n\n\0MESS"));
        var frames = r.Feed("AGE\ndestination:/notification/7/7\ncontent-type:application/json\n\n{\"data\":[]}\0\n");
        Assert.Single(frames);
        Assert.Equal("MESSAGE", frames[0].Command);
        Assert.Equal("/notification/7/7", frames[0].Header("destination"));
        Assert.Equal("{\"data\":[]}", frames[0].Body);
    }

    [Fact]
    public void Reader_returns_connected_from_combined_chunk()
    {
        var r = new StompFrameReader();
        var frames = r.Feed("CONNECTED\nversion:1.2\nuser-name:x\n\n\0");
        Assert.Single(frames);
        Assert.Equal("x", frames[0].Header("user-name"));
    }

    [Fact]
    public void Heartbeats_are_ignored()
    {
        var r = new StompFrameReader();
        Assert.Empty(r.Feed("\n"));
        Assert.Empty(r.Feed("\r\n"));
        var frames = r.Feed("\nCONNECTED\nuser-name:a\n\n\0");
        Assert.Single(frames);
    }

    [Fact]
    public void Family_is_read_from_destination()
    {
        Assert.Equal("123", AirCloudBackend.FamilyFromDestination("/notification/123/123"));
        Assert.Null(AirCloudBackend.FamilyFromDestination(null));
    }
}
