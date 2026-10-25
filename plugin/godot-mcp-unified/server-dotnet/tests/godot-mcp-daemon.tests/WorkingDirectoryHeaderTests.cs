using GodotMcp.Shim;
using Microsoft.AspNetCore.Http;

namespace GodotMcp.Daemon.Tests;

public sealed class WorkingDirectoryHeaderTests
{
    [Theory]
    [InlineData(@"D:\游戏\正式工程")]
    [InlineData(@"D:\游戏\项目 %20 空格")]
    [InlineData(@"D:\games\literal%20name")]
    public void working_directory_uses_ascii_headers_and_round_trips_without_double_decoding(string path)
    {
        var header = HttpForwarder.EncodeWorkingDirectory(path);
        Assert.All(header, character => Assert.InRange(character, '\0', '\x7f'));
        var context = new DefaultHttpContext();
        context.Request.Headers[SessionKeyMiddleware.WorkingDirectoryHeaderName] = header;
        Assert.Equal(path, SessionKeyMiddleware.ReadWorkingDirectory(context));
    }

    [Theory]
    [InlineData(@"D:\games\literal%20name")]
    [InlineData("")]
    public void legacy_plain_headers_keep_literal_percent_characters(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[SessionKeyMiddleware.WorkingDirectoryHeaderName] = path;
        Assert.Equal(path, SessionKeyMiddleware.ReadWorkingDirectory(context));
    }
}
