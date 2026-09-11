using AgenticColleague.AgentLogic;
using AgenticColleague.AgentLogic.ResponsesApi;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace AgenticColleague.Tests;

public sealed class IncomingMessagePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("<p>&nbsp;</p>")]
    public void EmptyTeamsMessageIsIgnored(string? text) =>
        Assert.True(IncomingMessagePolicy.ShouldIgnore(Message(text)));

    [Fact]
    public void HtmlOnlyEmptyAttachmentIsIgnored()
    {
        var message = Message(null);
        message.Attachments = [new Attachment { ContentType = "text/html", Content = "<div> </div>" }];
        Assert.True(IncomingMessagePolicy.ShouldIgnore(message));
    }

    [Theory]
    [InlineData("meetingStart")]
    [InlineData("meetingEnd")]
    public void StructuredLifecycleNotificationIsIgnored(string eventType)
    {
        var message = Message("Meeting notification");
        message.ChannelData = new { eventType };
        Assert.True(IncomingMessagePolicy.ShouldIgnore(message));
    }

    [Fact]
    public void SystemEventMessageIsIgnored()
    {
        var message = Message("Recording notification");
        message.ChannelData = new { messageType = "systemEventMessage" };
        Assert.True(IncomingMessagePolicy.ShouldIgnore(message));
    }

    [Theory]
    [InlineData("recap the meeting")]
    [InlineData("<at>Team Agent</at>")]
    [InlineData("<p>hello</p>")]
    public void UserTextIsPreserved(string text) =>
        Assert.False(IncomingMessagePolicy.ShouldIgnore(Message(text)));

    [Theory]
    [InlineData("application/vnd.microsoft.card.adaptive")]
    [InlineData("application/vnd.microsoft.teams.file.download.info")]
    [InlineData("image/png")]
    public void UserAttachmentsArePreserved(string contentType)
    {
        var message = Message(null);
        message.Attachments = [new Attachment { ContentType = contentType, Content = new { id = "file-1" } }];
        Assert.False(IncomingMessagePolicy.ShouldIgnore(message));
    }

    [Fact]
    public void CardSubmissionIsPreserved()
    {
        var message = Message(null);
        message.Value = new { action = "submit" };
        Assert.False(IncomingMessagePolicy.ShouldIgnore(message));
    }

    [Theory]
    [InlineData("email", "message")]
    [InlineData("msteams", "installationUpdate")]
    [InlineData("msteams", "invoke")]
    public void OtherActivityRoutesArePreserved(string channel, string type)
    {
        var message = Message(null);
        message.ChannelId = channel;
        message.Type = type;
        Assert.False(IncomingMessagePolicy.ShouldIgnore(message));
    }

    [Fact]
    public async Task RealServiceReturnsBeforeModelOrTurnSideEffectsForEmptyMessage()
    {
        var service = new ResponsesApiAgentLogicService(new() { UserId = Guid.NewGuid() },
            new ConfigurationBuilder().Build(), NullLogger.Instance, "not-used", []);
        var context = DispatchProxy.Create<ITurnContext, ActivityOnlyContext>();
        ((ActivityOnlyContext)(object)context).Activity = Message(null);
        await service.NewActivityReceived(context, null!, CancellationToken.None);
    }

    private static Activity Message(string? text) => new()
    {
        Type = ActivityTypes.Message, ChannelId = "msteams", Text = text
    };
}

public class ActivityOnlyContext : DispatchProxy
{
    public Activity Activity { get; set; } = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_Activity" ? Activity
            : throw new InvalidOperationException("Unexpected work on an ignored activity: " + targetMethod?.Name);
}
