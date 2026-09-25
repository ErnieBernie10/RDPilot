using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ClipboardSyncServiceTests
{
    private readonly ClipboardSyncService _service = new();

    [Fact]
    public void TryRememberText_ReturnsFalseForRepeatedSignature()
    {
        Assert.True(_service.TryRememberText("hello", out var signature));
        Assert.Equal("text:hello", signature);

        Assert.False(_service.TryRememberText("hello", out signature));
        Assert.Equal("text:hello", signature);
    }

    [Fact]
    public void TryRememberFiles_BuildsStableSignature()
    {
        Assert.True(_service.TryRememberFiles(["a.txt", "b.txt"], out var signature));
        Assert.Equal("files:a.txt\nb.txt", signature);
    }

    [Fact]
    public void BeginRemoteFilesUpdate_SuppressesLocalPolling()
    {
        Assert.True(_service.ShouldPollLocalClipboard);

        _service.BeginRemoteFilesUpdate(["remote.txt"]);
        Assert.False(_service.ShouldPollLocalClipboard);

        _service.EndRemoteUpdate();
        Assert.True(_service.ShouldPollLocalClipboard);
    }

    [Fact]
    public void BeginAndEndRemoteUpdate_SuppressesLocalPolling()
    {
        Assert.True(_service.ShouldPollLocalClipboard);

        _service.BeginRemoteTextUpdate("remote");
        Assert.False(_service.ShouldPollLocalClipboard);

        _service.EndRemoteUpdate();
        Assert.True(_service.ShouldPollLocalClipboard);
    }

    [Fact]
    public void ClearSignature_ReturnsTrueOnlyWhenStateWasPresent()
    {
        Assert.False(_service.ClearSignature());
        Assert.True(_service.TryRememberText("hello", out _));
        Assert.True(_service.ClearSignature());
        Assert.False(_service.ClearSignature());
    }

    [Fact]
    public void TransientEmptyRead_DoesNotEchoRemoteText()
    {
        _service.BeginRemoteTextUpdate("remote");
        _service.EndRemoteUpdate();

        Assert.False(_service.ObserveEmptyClipboard());
        Assert.False(_service.TryRememberText("remote", out _));
    }

    [Fact]
    public void ConsecutiveEmptyReads_ClearPreviousOfferOnce()
    {
        _service.TryRememberFiles(["file.txt"], out _);

        Assert.False(_service.ObserveEmptyClipboard());
        Assert.True(_service.ObserveEmptyClipboard());
        Assert.False(_service.ObserveEmptyClipboard());
        Assert.True(_service.TryRememberText("new text", out _));
    }

    [Fact]
    public void OverlappingRemoteUpdates_KeepPollingSuppressedUntilBothFinish()
    {
        _service.BeginRemoteTextUpdate("first");
        _service.BeginRemoteFilesUpdate(["second.txt"]);

        _service.EndRemoteUpdate();
        Assert.False(_service.ShouldPollLocalClipboard);
        _service.EndRemoteUpdate();
        Assert.True(_service.ShouldPollLocalClipboard);
        Assert.False(_service.TryRememberFiles(["second.txt"], out _));
    }

    [Fact]
    public void SwitchingSessions_ReoffersUnchangedClipboardContent()
    {
        var first = new object();
        var second = new object();
        _service.UseSession(first);
        Assert.True(_service.TryRememberText("copy", out _));
        Assert.False(_service.TryRememberText("copy", out _));

        _service.UseSession(second);
        Assert.True(_service.TryRememberText("copy", out _));
        _service.UseSession(second);
        Assert.False(_service.TryRememberText("copy", out _));
    }
}
