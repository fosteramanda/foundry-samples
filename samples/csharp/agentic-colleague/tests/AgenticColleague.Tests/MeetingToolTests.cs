using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgenticColleague.AgentLogic;
using AgenticColleague.AgentLogic.ResponsesApi.Helpers;
using AgenticColleague.Models;
using AgenticColleague.Services;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticColleague.Tests;

public sealed class MeetingToolTests
{
    [Fact]
    public async Task RecapFetchesTranscriptWithoutTrackingApprovalOrNoticeWrites()
    {
        using var fixture = new MeetingFixture();
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.StartsWith("WEBVTT", result["content"]!.GetValue<string>());
        Assert.Equal("https://outlook.office.com/calendar/item/event-1", result["source_url"]!.GetValue<string>());
        Assert.True(result["attribution_available"]!.GetValue<bool>());
        Assert.Empty(fixture.Store.Entities);
        Assert.All(fixture.Transport.Requests, uri => Assert.StartsWith(MeetingFixture.OwnPath, uri.AbsolutePath));
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/manager"));
        Assert.Contains(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/content"));
    }

    [Fact]
    public void PromptAndDefinitionsRemoveTheDuplicateApprovalConversation()
    {
        using var fixture = new MeetingFixture();
        var names = fixture.Handler.GetToolDefinitions().Select(n => n["name"]!.GetValue<string>()).ToList();
        Assert.Equal(4, names.Count);
        Assert.DoesNotContain("record_capture_notice", names);
        var prompt = AgentInstructions.GetInstructions(fixture.Metadata, meetingRegistryEnabled: true);
        Assert.Contains("Do not ask the organizer to approve capture", prompt);
        Assert.DoesNotContain("record_capture_notice", prompt);
        Assert.DoesNotContain("attendees_notified", prompt);
        Assert.DoesNotContain("Workstream Manager", prompt);
        Assert.All(names, name => Assert.Contains(name, prompt));
        Assert.Contains("Meeting retrieval unavailable", AgentInstructions.GetInstructions(fixture.Metadata));
    }

    [Fact]
    public async Task CalendarPaginationFindsLaterPageOccurrence()
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/calendarView")
            ? MeetingFixture.Json(uri.Query.Contains("skiptoken")
                ? MeetingFixture.Page(MeetingFixture.Event())
                : MeetingFixture.Page([], MeetingFixture.GraphBase + MeetingFixture.OwnPath + "/calendarView?$skiptoken=page2"))
            : null;
        var result = await fixture.Call("read_meeting_transcript", """{"subject":"Planning"}""");
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal(4, fixture.Transport.Requests.Count(uri => uri.AbsolutePath.EndsWith("/calendarView")));
    }

    [Theory]
    [InlineData("https://example.test/v1.0/users/11111111-1111-1111-1111-111111111111/calendarView")]
    [InlineData("http://graph.microsoft.com/v1.0/users/11111111-1111-1111-1111-111111111111/calendarView")]
    [InlineData("https://graph.microsoft.com:444/v1.0/users/11111111-1111-1111-1111-111111111111/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/users/22222222-2222-2222-2222-222222222222/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/users/11111111-1111-1111-1111-111111111111/events/%2E%2E/%2E%2E/other/calendarView")]
    public async Task UnsafeNextLinksAreRejectedBeforeSendingToken(string next)
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/calendarView")
            ? MeetingFixture.Json(MeetingFixture.Page([], next)) : null;
        var result = await fixture.Call("list_tracked_meetings");
        Assert.Equal("invalid_graph_link", result["status"]!.GetValue<string>());
        Assert.Equal(2, fixture.Transport.Requests.Count);
    }

    [Fact]
    public async Task RepeatedNextLinkIsAnErrorNotAPartialCalendar()
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/calendarView")
            ? MeetingFixture.Json(MeetingFixture.Page([], MeetingFixture.GraphBase + MeetingFixture.OwnPath + "/calendarView?loop=1"))
            : null;
        Assert.Equal("incomplete_results", (await fixture.Call("list_tracked_meetings"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SameSubjectRequiresAnOccurrenceRatherThanPickingFirst()
    {
        using var fixture = new MeetingFixture();
        fixture.Events.Add(MeetingFixture.Event("event-2"));
        var result = await fixture.Call("read_meeting_transcript", """{"subject":"Planning"}""");
        Assert.Equal("ambiguous_meeting", result["status"]!.GetValue<string>());
        Assert.Equal(2, result["candidates"]!.AsArray().Count);
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.Contains("/onlineMeetings"));
    }

    [Fact]
    public async Task UntitledMeetingsAndModernJoinUrlsAreReadableById()
    {
        using var fixture = new MeetingFixture();
        fixture.Events[0]!["subject"] = "";
        fixture.Events[0]!["onlineMeeting"]!["joinUrl"] = "https://teams.microsoft.com/meet/123456?p=example";
        var listing = await fixture.Call("list_tracked_meetings");
        Assert.Equal("(untitled meeting)", listing["meetings"]![0]!["subject"]!.GetValue<string>());
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("ok", result["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABookmarkDoesNotBypassTheFreshCalendarRead()
    {
        using var fixture = new MeetingFixture();
        await fixture.Call("track_meeting", """{"event_id":"event-1"}""");
        fixture.Transport.Override = uri => uri.AbsolutePath.Contains("/events/")
            ? MeetingFixture.Error(HttpStatusCode.NotFound, "ErrorItemNotFound") : null;
        Assert.Equal("graph_error", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/content"));
    }

    [Fact]
    public async Task DateWindowAndCalendarTimesDoNotUseMachineLocalZone()
    {
        using var fixture = new MeetingFixture();
        fixture.Events[0]!["start"] = new JsonObject { ["dateTime"] = "2026-09-20T10:00:00", ["timeZone"] = "Pacific Standard Time" };
        fixture.Events[0]!["end"] = new JsonObject { ["dateTime"] = "2026-09-20T11:00:00", ["timeZone"] = "Pacific Standard Time" };
        var result = await fixture.Call("list_tracked_meetings", """{"on_date":"2026-09-20"}""");
        Assert.Equal("2026-09-20T10:00:00-07:00", result["meetings"]![0]!["start"]!.GetValue<string>());
        var request = Assert.Single(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/calendarView"));
        Assert.Contains("startDateTime=2026-09-20T07:00:00", Uri.UnescapeDataString(request.Query));
        Assert.Contains("endDateTime=2026-09-21T07:00:00", Uri.UnescapeDataString(request.Query));
    }

    [Theory]
    [InlineData("""{"on_date":"yesterday"}""")]
    [InlineData("""{"on_date":"2026-02-30"}""")]
    [InlineData("""{"on_or_after":"2026-09-20","on_or_before":"2026-09-19"}""")]
    [InlineData("""{"on_or_after":"2020-01-01","on_or_before":"2026-09-19"}""")]
    [InlineData("""{"on_date":false}""")]
    public async Task InvalidDatesNeverSilentlySelectADifferentWindow(string args)
    {
        using var fixture = new MeetingFixture();
        Assert.Equal("invalid_arguments", (await fixture.Call("list_tracked_meetings", args))["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/calendarView"));
    }

    [Fact]
    public async Task RecurrenceSelectionUsesTimingAndDurationNotResponseOrder()
    {
        using var fixture = new MeetingFixture();
        fixture.Transcripts.Clear();
        fixture.Transcripts.Add(MeetingFixture.Segment("main", "2026-09-20T17:02:00Z", "2026-09-20T17:55:00Z"));
        fixture.Transcripts.Add(MeetingFixture.Segment("join", "2026-09-20T17:00:00Z", "2026-09-20T17:01:00Z"));
        fixture.Transcripts.Add(MeetingFixture.Segment("wrong-day", "2026-09-19T17:00:00Z", "2026-09-19T19:00:00Z"));
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("main", result["transcript_id"]!.GetValue<string>());
        Assert.Equal(2, result["available_segments"]!.GetValue<int>());
        Assert.Equal("longest segment of this occurrence", result["coverage"]!.GetValue<string>());
        Assert.EndsWith("/transcripts/main/content", fixture.Transport.Requests.Last().AbsolutePath);
    }

    [Fact]
    public async Task TranscriptPaginationFindsTheCorrectOccurrence()
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/transcripts")
            ? MeetingFixture.Json(uri.Query.Contains("skiptoken")
                ? MeetingFixture.Page(MeetingFixture.Segment())
                : MeetingFixture.Page(
                    [MeetingFixture.Segment("old", "2026-09-19T17:00:00Z", "2026-09-19T18:00:00Z")],
                    MeetingFixture.GraphBase + MeetingFixture.OwnPath + "/onlineMeetings/meeting-1/transcripts?$skiptoken=page2"))
            : null;
        Assert.Equal("ok", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
        Assert.Equal(2, fixture.Transport.Requests.Count(uri => uri.AbsolutePath.EndsWith("/transcripts")));
    }

    [Fact]
    public async Task FilterEscapesODataQuotesAndQueryCharacters()
    {
        using var fixture = new MeetingFixture();
        fixture.Events[0]!["onlineMeeting"]!["joinUrl"] = "https://teams.microsoft.com/meet/o'hare?p=1&context={a}";
        await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        var request = Assert.Single(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/onlineMeetings"));
        Assert.Contains("JoinWebUrl eq 'https://teams.microsoft.com/meet/o''hare?p=1&context={a}'",
            Uri.UnescapeDataString(request.Query));
        Assert.DoesNotContain("&context=", request.Query);
    }

    [Fact]
    public async Task TranscriptPathIdsAreEncodedAsSegments()
    {
        using var fixture = new MeetingFixture();
        fixture.Transcripts[0]!["id"] = "transcript/with?characters";
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Contains("transcript%2Fwith%3Fcharacters/content", fixture.Transport.Requests.Last().AbsoluteUri);
    }

    [Fact]
    public async Task NoMatchingTranscriptDoesNotClaimWhyItIsMissing()
    {
        using var fixture = new MeetingFixture();
        fixture.Transcripts.Clear();
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("no_transcript", result["status"]!.GetValue<string>());
        Assert.Contains("processing", result["message"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/content"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailureIsNotAnEmptyTranscript(int status)
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/transcripts")
            ? MeetingFixture.Error((HttpStatusCode)status, "example_failure") : null;
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("graph_error", result["status"]!.GetValue<string>());
        Assert.Contains($"HTTP {status}", result["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisabledTenantApiIsReportedPrecisely()
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/transcripts")
            ? MeetingFixture.Error(HttpStatusCode.Forbidden, "GraphAccessToTranscriptsDisabled") : null;
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Contains("tenant's Teams transcript API access is disabled", result["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Sign in</html>")]
    [InlineData("WEBVTT\n\n")]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:01.000\n<v > </v>\n")]
    public async Task EmptyOrInvalidContentCannotSupportARecap(string vtt)
    {
        using var fixture = new MeetingFixture();
        fixture.Vtt = vtt;
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("invalid_transcript_content", result["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SpeakerlessTranscriptDoesNotInventAttribution()
    {
        using var fixture = new MeetingFixture();
        fixture.Vtt = "WEBVTT\n\n00:00.000 --> 00:01.000\n<v >An unattributed decision.</v>\n";
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.False(result["attribution_available"]!.GetValue<bool>());
        Assert.Empty(result["speakers"]!.AsArray());
    }

    [Fact]
    public async Task OversizedTranscriptIsExplicitlyPartialAndBounded()
    {
        using var fixture = new MeetingFixture();
        fixture.Vtt += new string('a', MeetingGraphClient.MaxTranscriptCharacters);
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(MeetingGraphClient.MaxTranscriptCharacters, result["content"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task ExplicitExclusionBlocksContentButOnlyForItsOccurrence()
    {
        using var fixture = new MeetingFixture();
        fixture.Handler.SetCurrentActor(MeetingFixture.ManagerId);
        Assert.Equal("ok", (await fixture.Call("set_meeting_capture",
            """{"event_id":"event-1","approved":false}"""))["status"]!.GetValue<string>());
        var excluded = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Equal("meeting_excluded", excluded["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/content"));
        fixture.Events.Add(MeetingFixture.Event("event-2"));
        fixture.Events[1]!["start"]!["dateTime"] = "2026-09-20T18:00:00";
        fixture.Events[1]!["end"]!["dateTime"] = "2026-09-20T19:00:00";
        fixture.Transcripts.Clear();
        fixture.Transcripts.Add(MeetingFixture.Segment("second", "2026-09-20T18:05:00Z", "2026-09-20T18:50:00Z"));
        Assert.Equal("ok", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-2"}"""))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task NonManagerCannotRemoveAnExclusion()
    {
        using var fixture = new MeetingFixture();
        fixture.Handler.SetCurrentActor("44444444-4444-4444-4444-444444444444");
        Assert.Equal("manager_required", (await fixture.Call("set_meeting_capture",
            """{"event_id":"event-1","approved":true}"""))["status"]!.GetValue<string>());
        Assert.Empty(fixture.Store.Entities);
    }

    [Fact]
    public async Task LegacyRestrictionIsPreservedUntilAnExplicitManagerChange()
    {
        using var fixture = new MeetingFixture();
        fixture.Store.Entities.Add("thread-1", new TrackedMeetingEntity { RowKey = "thread-1", CaptureApproved = false });
        Assert.Equal("meeting_excluded", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
        await fixture.Call("track_meeting", """{"event_id":"event-1"}""");
        Assert.Equal("meeting_excluded", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
        fixture.Handler.SetCurrentActor(MeetingFixture.ManagerId);
        await fixture.Call("set_meeting_capture", """{"event_id":"event-1","approved":true}""");
        Assert.Equal("ok", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
        Assert.All(fixture.Store.Entities.Values, entity =>
        {
            Assert.False(entity.CaptureApproved);
            Assert.Null(entity.NoticeSentUtc);
        });
    }

    [Theory]
    [InlineData("read_meeting_transcript", """{"event_id":"event-1"}""")]
    [InlineData("list_tracked_meetings", "{}")]
    public async Task StorageFailuresDoNotBecomeMissingMeetings(string name, string args)
    {
        using var fixture = new MeetingFixture();
        fixture.Store.FailReads = true;
        var result = await fixture.Call(name, args);
        Assert.Equal("registry_unavailable", result["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/content"));
    }

    [Fact]
    public async Task FailedPreferenceWriteIsNotReportedAsSuccess()
    {
        using var fixture = new MeetingFixture();
        fixture.Store.FailWrites = true;
        Assert.Equal("registry_write_failed", (await fixture.Call("track_meeting",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task TranscriptSpanningAdjacentOccurrencesIsNotMisattributed()
    {
        using var fixture = new MeetingFixture();
        fixture.Events.Add(MeetingFixture.Event("previous"));
        fixture.Events[1]!["start"]!["dateTime"] = "2026-09-20T16:00:00";
        fixture.Events[1]!["end"]!["dateTime"] = "2026-09-20T17:00:00";
        fixture.Transcripts[0]!["createdDateTime"] = "2026-09-20T16:30:00Z";
        Assert.Equal("ambiguous_transcript", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.EndsWith("/content"));
    }

    [Fact]
    public async Task EarlyRecordingIsUsableWhenItOnlyOverlapsOneOccurrence()
    {
        using var fixture = new MeetingFixture();
        fixture.Transcripts[0]!["createdDateTime"] = "2026-09-20T16:59:00Z";
        Assert.Equal("ok", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1"}"""))["status"]!.GetValue<string>());
    }

    [Fact]
    public void LocalToolContractsInvalidateOldResponseChains()
    {
        using var fixture = new MeetingFixture();
        var tools = fixture.Handler.GetToolDefinitions();
        var current = ResponsesApiClient.ComputeToolFingerprint([], tools);
        Assert.NotEqual(ResponsesApiClient.ComputeToolFingerprint([], []), current);
        Assert.Equal(current, ResponsesApiClient.ComputeToolFingerprint([], tools.AsEnumerable().Reverse()));
        var retiredTool = JsonNode.Parse("""{"type":"function","name":"record_capture_notice"}""")!;
        Assert.NotEqual(current, ResponsesApiClient.ComputeToolFingerprint([], tools.Append(retiredTool)));
    }

    [Fact]
    public async Task EventIdCannotOverrideAnExplicitRequestedDate()
    {
        using var fixture = new MeetingFixture();
        Assert.Equal("date_mismatch", (await fixture.Call("read_meeting_transcript",
            """{"event_id":"event-1","on_date":"2026-09-19"}"""))["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Transport.Requests, uri => uri.AbsolutePath.Contains("/onlineMeetings"));
    }

    [Fact]
    public async Task MalformedNextLinkIsNotAnEmptyFinalPage()
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri => uri.AbsolutePath.EndsWith("/calendarView")
            ? MeetingFixture.Json(new JsonObject { ["value"] = new JsonArray(), ["@odata.nextLink"] = 17 }) : null;
        Assert.Equal("invalid_graph_response", (await fixture.Call("list_tracked_meetings"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RetryAfterIsReportedWhenGraphThrottles()
    {
        using var fixture = new MeetingFixture();
        fixture.Transport.Override = uri =>
        {
            if (!uri.AbsolutePath.EndsWith("/transcripts")) { return null; }
            var response = MeetingFixture.Error(HttpStatusCode.TooManyRequests, "TooManyRequests");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };
        var result = await fixture.Call("read_meeting_transcript", """{"event_id":"event-1"}""");
        Assert.Contains("Retry-After: 30", result["message"]!.GetValue<string>());
    }
}

internal sealed class MeetingFixture : IDisposable
{
    public const string GraphBase = "https://graph.microsoft.com";
    public const string OwnPath = "/v1.0/users/11111111-1111-1111-1111-111111111111";
    public const string ManagerId = "33333333-3333-3333-3333-333333333333";
    public AgentMetadata Metadata { get; } = new() { UserId = Guid.Parse("11111111-1111-1111-1111-111111111111") };
    public JsonArray Events { get; } = [Event()];
    public JsonArray Transcripts { get; } = [Segment()];
    public string Vtt { get; set; } = "WEBVTT\n\n00:00.000 --> 00:01.000\n<v Speaker One>A decision.</v>\n";
    public MemoryMeetingStore Store { get; } = new();
    public RecordingTransport Transport { get; }
    public MeetingRegistryToolHandler Handler { get; }
    private readonly HttpClient _client;

    public MeetingFixture()
    {
        Transport = new RecordingTransport(Respond);
        _client = new HttpClient(Transport);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["MeetingDisplayTimeZone"] = "Pacific Standard Time" }).Build();
        Handler = new MeetingRegistryToolHandler(Metadata, NullLogger.Instance, _client, config,
            "test-token", Store, new FixedClock());
    }

    public async Task<JsonObject> Call(string name, string args = "{}") =>
        JsonNode.Parse((await Handler.TryExecuteAsync(name, args))!)!.AsObject();

    private HttpResponseMessage Respond(Uri uri)
    {
        if (uri.AbsolutePath == OwnPath)
        {
            return Json(new JsonObject { ["mail"] = "agent@example.test", ["id"] = Metadata.UserId.ToString() });
        }
        if (uri.AbsolutePath.EndsWith("/manager")) { return Json(new JsonObject { ["id"] = ManagerId }); }
        if (uri.AbsolutePath.EndsWith("/calendarView")) { return Json(Page(Events)); }
        if (uri.AbsolutePath.Contains("/events/"))
        {
            var eventId = Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.IndexOf("/events/") + 8)..]);
            return Json(Events.Single(e => e!["id"]!.GetValue<string>() == eventId)!.DeepClone());
        }
        if (uri.AbsolutePath.EndsWith("/onlineMeetings"))
        {
            return Json(Page(new JsonObject { ["id"] = "meeting-1" }));
        }
        if (uri.AbsolutePath.EndsWith("/transcripts")) { return Json(Page(Transcripts)); }
        if (uri.AbsolutePath.EndsWith("/content"))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Vtt, Encoding.UTF8, "text/vtt") };
        }
        throw new InvalidOperationException("Unexpected Graph route: " + uri);
    }

    public static JsonObject Event(string id = "event-1") => new()
    {
        ["id"] = id, ["subject"] = "Planning", ["isCancelled"] = false,
        ["start"] = new JsonObject { ["dateTime"] = "2026-09-20T17:00:00", ["timeZone"] = "UTC" },
        ["end"] = new JsonObject { ["dateTime"] = "2026-09-20T18:00:00", ["timeZone"] = "UTC" },
        ["onlineMeeting"] = new JsonObject { ["joinUrl"] = "https://teams.microsoft.com/l/meetup-join/thread-1/0" },
        ["webLink"] = "https://outlook.office.com/calendar/item/" + id,
        ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "organizer@example.test" } }
    };

    public static JsonObject Segment(string id = "transcript-1",
        string start = "2026-09-20T17:05:00Z", string end = "2026-09-20T17:50:00Z") => new()
    {
        ["id"] = id, ["createdDateTime"] = start, ["endDateTime"] = end
    };

    public static JsonObject Page(JsonNode item) => new() { ["value"] = new JsonArray(item.DeepClone()) };
    public static JsonObject Page(JsonArray items, string? next = null) => new()
    {
        ["value"] = items.DeepClone(), ["@odata.nextLink"] = next
    };
    public static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")
    };
    public static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["code"] = code } }.ToJsonString())
    };
    public void Dispose() => _client.Dispose();
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-21T12:00:00Z");
}

internal sealed class RecordingTransport(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public Func<Uri, HttpResponseMessage?>? Override { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Contains("outlook.timezone=\"UTC\"", request.Headers.GetValues("Prefer").Single());
        Requests.Add(request.RequestUri!);
        return Task.FromResult(Override?.Invoke(request.RequestUri!) ?? respond(request.RequestUri!));
    }
}

internal sealed class MemoryMeetingStore : IMeetingRegistryStore
{
    public bool IsAvailable => true;
    public Dictionary<string, TrackedMeetingEntity> Entities { get; } = [];
    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }
    public Task<TrackedMeetingEntity?> GetAsync(string mailbox, string key)
    {
        if (FailReads) { throw new RequestFailedException(503, "Test storage failure"); }
        return Task.FromResult(Entities.GetValueOrDefault(MeetingRegistryStore.ToRowKey(key)));
    }
    public Task<bool> UpsertAsync(TrackedMeetingEntity entity)
    {
        if (FailWrites) { return Task.FromResult(false); }
        entity.ETag = new ETag("test-etag");
        Entities[entity.RowKey] = entity;
        return Task.FromResult(true);
    }
    public Task<List<TrackedMeetingEntity>> ListAsync(string mailbox)
    {
        if (FailReads) { throw new RequestFailedException(503, "Test storage failure"); }
        return Task.FromResult(Entities.Values.ToList());
    }
}
