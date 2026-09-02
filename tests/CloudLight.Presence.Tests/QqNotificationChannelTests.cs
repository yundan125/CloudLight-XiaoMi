using System.Net;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Notifications;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class QqNotificationChannelTests
{
    [Theory]
    [InlineData(NotificationTargetType.Private, "private-user", "/v2/users/openid/messages")]
    [InlineData(NotificationTargetType.Group, "group", "/v2/groups/group-openid/messages")]
    public void TargetTypeUsesAnExplicitMessageEndpoint(NotificationTargetType targetType, string category, string path)
    {
        Assert.Equal(category, QQNotificationEndpoint.CategoryFor(targetType));
        Assert.Equal(path, QQNotificationEndpoint.BuildMessagePath(targetType, targetType == NotificationTargetType.Private ? "openid" : "group-openid"));
    }

    [Fact]
    public void ApiErrorParserReadsOfficialErrorFieldsAndHandlesMalformedResponses()
    {
        var parsed = QQNotificationApiErrorParser.Parse("{\"err_code\":40011028,\"message\":\"资源不存在\",\"trace_id\":\"trace-123\"}");
        Assert.Equal(40011028, parsed.ErrorCode);
        Assert.Equal("资源不存在", parsed.Message);
        Assert.Equal("trace-123", parsed.TraceId);

        var malformed = QQNotificationApiErrorParser.Parse("not-json");
        Assert.Null(malformed.ErrorCode);
        Assert.Null(malformed.Message);
        Assert.Null(malformed.TraceId);
    }

    [Fact]
    public async Task SendRejectsMaskedTargetBeforeAnyNetworkRequest()
    {
        await using var channel = new QQNotificationChannel();

        var result = await channel.SendAsync(
            new NotificationRequest(0, 0, 0, "test", NotificationChannelType.QQ, NotificationTargetType.Private,
                "C47****FD8", "message", DateTimeOffset.UtcNow),
            0,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.InvalidRequest, result.FailureKind);
        Assert.Contains("完整 OpenID", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, null, "private-user", NotificationFailureKind.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, null, "private-user", NotificationFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, null, "private-user", NotificationFailureKind.Authentication)]
    [InlineData(HttpStatusCode.NotFound, null, "private-user", NotificationFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.TooManyRequests, null, "private-user", NotificationFailureKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, null, "private-user", NotificationFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, 40011028, "private-user", NotificationFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.BadRequest, 40054004, "private-user", NotificationFailureKind.PermanentTarget)]
    [InlineData(HttpStatusCode.BadRequest, 40054013, "private-user", NotificationFailureKind.PermanentTarget)]
    [InlineData(HttpStatusCode.BadRequest, 40034101, "group", NotificationFailureKind.PermanentTarget)]
    [InlineData(HttpStatusCode.BadRequest, 40054003, "group", NotificationFailureKind.PermanentTarget)]
    [InlineData(HttpStatusCode.BadRequest, 40054006, "private-user", NotificationFailureKind.Transient)]
    public void ApiFailuresAreClassifiedWithoutUsingTheHumanMessage(
        HttpStatusCode status,
        int? qqErrorCode,
        string endpointCategory,
        NotificationFailureKind expected)
    {
        Assert.Equal(expected, QQNotificationFailureClassifier.Classify(status, qqErrorCode, endpointCategory));
    }
}
