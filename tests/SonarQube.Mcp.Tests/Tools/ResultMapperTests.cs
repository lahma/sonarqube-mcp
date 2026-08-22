using System.Text.Json;

using SonarQube.Mcp.Http.Models;
using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// The mapper, driven directly with the wire shapes: the <c>components[]</c> join, the deep links
/// nothing in the API returns, and the three places where the same field name means two different
/// things.
/// </summary>
/// <remarks>
/// These are the claims that no request assertion can make. Whether <c>files=</c> is sent correctly
/// is visible in a URL; whether an issue on a component the sidecar forgot still reports a usable
/// path is only visible here.
/// </remarks>
public class ResultMapperTests
{
    private const string Project = ToolTestHost.Project;
    private const string BaseUrl = "https://sonarcloud.io";
    private const string FileKey = ToolTestHost.FileComponent;

    /// <summary>
    /// The single biggest context saving in the tool layer, asserted over a whole live page: every
    /// issue gets a path, and no path is a component key in disguise.
    /// </summary>
    [Fact]
    public void EveryIssueOnALivePageGetsItsPathFromTheSidecar()
    {
        var response = Issues("issues-search-page.json");

        var result = ResultMapper.Issues(response, default, 1, 2, BaseUrl);

        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, issue => Assert.False(string.IsNullOrEmpty(issue.File)));
        Assert.All(result.Issues, issue => Assert.DoesNotContain(':', issue.File!));

        // The key is still there when the caller needs one back.
        Assert.All(result.Issues, issue => Assert.Contains(':', issue.Component!));
    }

    /// <summary>
    /// The sidecar is SonarQube's, not ours. A response that listed the same component twice would
    /// take down a whole issue search over a detail no caller can see.
    /// </summary>
    [Fact]
    public void ADuplicateComponentKeyInTheSidecarDoesNotThrow()
    {
        var response = JsonSerializer.Deserialize(
            ToolPayloads.IssuesWithDuplicateComponents,
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var result = ResultMapper.Issues(response, default, 1, 2, BaseUrl);

        Assert.Equal(2, result.Issues.Count);
        Assert.Equal("src/Quartz/Widget.cs", result.Issues[0].File);

        // The second issue's component is not in the sidecar at all, so it falls back.
        Assert.Equal("src/Quartz/Other.cs", result.Issues[1].File);
    }

    /// <summary>
    /// With no sidecar the path is the substring after the first colon, which is what a component
    /// key is made of — right for every file component.
    /// </summary>
    [Fact]
    public void WithNoSidecarThePathIsTheSubstringAfterTheFirstColon()
    {
        Assert.Equal(
            "src/Quartz/Core/QuartzScheduler.cs",
            ComponentKeys.PathFor(FileKey, ComponentKeys.NoComponents));
    }

    /// <summary>A project component's key has no path part, so the key itself is the honest answer.</summary>
    [Fact]
    public void AKeyWithNoPathPartResolvesToItself()
    {
        Assert.Equal(Project, ComponentKeys.PathFor(Project, ComponentKeys.NoComponents));
        Assert.Equal("proj:", ComponentKeys.PathFor("proj:", ComponentKeys.NoComponents));
        Assert.Equal(string.Empty, ComponentKeys.PathFor(null, ComponentKeys.NoComponents));
    }

    /// <summary>
    /// A project entry in the sidecar has no <c>path</c>; falling back to its name is what keeps a
    /// project-level issue from reporting an empty file.
    /// </summary>
    [Fact]
    public void AComponentWithNoPathFallsBackToItsLongNameThenItsName()
    {
        var map = ComponentKeys.BuildPathMap(
        [
            new ComponentDto { Key = "a", LongName = "the/long/name" },
            new ComponentDto { Key = "b", Name = "the-name" },
            new ComponentDto { Key = "c" },
            new ComponentDto { Path = "orphan/without/a/key.cs" },
        ]);

        Assert.Equal("the/long/name", map["a"]);
        Assert.Equal("the-name", map["b"]);
        Assert.False(map.ContainsKey("c"));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void AnAbsentSidecarProducesTheSharedEmptyMap()
    {
        Assert.Same(ComponentKeys.NoComponents, ComponentKeys.BuildPathMap(null));
        Assert.Same(ComponentKeys.NoComponents, ComponentKeys.BuildPathMap([]));
    }

    // ---------------------------------------------------------------------------------------
    // Deep links
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// SonarQube returns no web URL for anything, so every link is composed — and a link that
    /// forgets the scope opens the main branch, where the issue being linked to may not exist.
    /// </summary>
    [Fact]
    public void AnIssueLinkCarriesTheBranchTheSearchWasScopedTo()
    {
        var response = Issues("issues-search-single.json");

        var result = ResultMapper.Issues(response, new AnalysisScope("release/4.0", null), 1, 1, BaseUrl);

        Assert.Equal(
            "https://sonarcloud.io/project/issues?id=quartznet_quartznet" +
            "&issues=AZ_xePOumT_q4T_1FWf8&open=AZ_xePOumT_q4T_1FWf8&branch=release%2F4.0",
            result.Issues[0].Url);
    }

    [Fact]
    public void AnIssueLinkCarriesThePullRequestInstead()
    {
        var response = Issues("issues-search-single.json");

        var result = ResultMapper.Issues(response, new AnalysisScope(null, "3266"), 1, 1, BaseUrl);

        Assert.EndsWith("&pullRequest=3266", result.Issues[0].Url!, StringComparison.Ordinal);
        Assert.DoesNotContain("branch=", result.Issues[0].Url!, StringComparison.Ordinal);
    }

    /// <summary>A component key carries a colon and slashes, none of which may travel raw.</summary>
    [Fact]
    public void ComponentKeysAreEscapedExactlyOnceInALink()
    {
        var url = ComponentKeys.ComponentUrl(BaseUrl, Project, FileKey, default);

        Assert.Equal(
            "https://sonarcloud.io/code?id=quartznet_quartznet" +
            "&selected=quartznet_quartznet%3Asrc%2FQuartz%2FCore%2FQuartzScheduler.cs",
            url);
    }

    [Fact]
    public void AHotspotLinkNamesTheProjectAndTheHotspot()
    {
        Assert.Equal(
            "https://sonarcloud.io/security_hotspots?id=quartznet_quartznet&hotspots=AZvu8Zyf&pullRequest=3266",
            ComponentKeys.HotspotUrl(BaseUrl, Project, "AZvu8Zyf", new AnalysisScope(null, "3266")));
    }

    [Fact]
    public void ALinkToNothingInParticularIsStillAValidProjectLink()
    {
        Assert.Equal(
            "https://sonarcloud.io/project/issues?id=quartznet_quartznet",
            ComponentKeys.IssueUrl(BaseUrl, Project, null, default));

        Assert.Equal(
            "https://sonarcloud.io/code?id=quartznet_quartznet",
            ComponentKeys.ComponentUrl(BaseUrl, Project, null, default));
    }

    // ---------------------------------------------------------------------------------------
    // The hotspot shapes: one field name, two meanings (C7) and one singular array (C8)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// C7: the search endpoint's <c>assignee</c> is an internal UUID and the show endpoint's is a
    /// login. Same name, different meaning — so the result records must not share one.
    /// </summary>
    [Fact]
    public void TheSearchEndpointsAssigneeIsAUuidAndTheShowEndpointsIsALogin()
    {
        var search = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-search-page.json"),
            SonarWireJsonContext.Default.HotspotsSearchResponseDto)!;

        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var summary = ResultMapper.Hotspots(search, Project, default, 1, 2, BaseUrl).Hotspots[0];
        var detail = ResultMapper.Hotspot(show, BaseUrl);

        Assert.Equal("AYgE5F7pEoXHSow6lKjD", summary.AssigneeId);
        Assert.Equal("lahma@github", detail.Assignee);

        // The same hotspot, so the difference is the endpoint's and not the entity's.
        Assert.Equal(summary.Key, detail.Key);
    }

    /// <summary>
    /// C8: the array is spelled <c>comment</c>, singular. The obvious plural deserialises to
    /// <see langword="null"/> with no error, which is the worst possible failure mode — so the
    /// payload below carries both and only the singular one may be read.
    /// </summary>
    [Fact]
    public void HotspotReviewCommentsAreReadFromTheSingularProperty()
    {
        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show-with-comment.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var detail = ResultMapper.Hotspot(show, BaseUrl);

        var comment = Assert.Single(detail.Comments);

        Assert.Equal("AaAoPgsRCRl6zX9b36TK", comment.Key);
        Assert.Equal("sonarqube-mcp verification - restored to original state", comment.Text);
        Assert.Equal("lahma@github", comment.Author);
    }

    /// <summary>
    /// The other half of C8, which no capture can carry: SonarQube never sends the plural spelling,
    /// so the only way to prove it is not read is to send both and watch the plural be ignored.
    /// </summary>
    [Fact]
    public void APluralCommentsPropertyOnAHotspotIsIgnored()
    {
        var show = JsonSerializer.Deserialize(
            ToolPayloads.HotspotShowWithBothCommentSpellings,
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var detail = ResultMapper.Hotspot(show, BaseUrl);

        Assert.Equal("the singular property", Assert.Single(detail.Comments).Text);
        Assert.DoesNotContain(detail.Comments, entry => entry.Key == "ignored");
    }

    /// <summary>
    /// A live changelog, which is richer than the obvious shape in two ways: a status change carries
    /// <em>two</em> diffs (status and resolution move together), and a diff that clears a value has
    /// an <c>oldValue</c> with no <c>newValue</c>. The first entry is SonarQube's own severity
    /// recalculation and has no user at all.
    /// </summary>
    [Fact]
    public void AHotspotsReviewHistoryIsMappedFieldByField()
    {
        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show-with-comment.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var changelog = ResultMapper.Hotspot(show, BaseUrl).Changelog;

        var automatic = changelog[0];
        Assert.Null(automatic.User);
        Assert.Null(automatic.UserName);
        Assert.Equal("severity", Assert.Single(automatic.Changes).Field);

        var review = changelog[1];
        Assert.Equal("lahma@github", review.User);
        Assert.Equal("Marko Lahma", review.UserName);

        var status = review.Changes.Single(change => change.Field == "status");
        Assert.Equal("TO_REVIEW", status.OldValue);
        Assert.Equal("REVIEWED", status.NewValue);

        // Set, so there is a newValue and nothing before it.
        var resolution = review.Changes.Single(change => change.Field == "resolution");
        Assert.Null(resolution.OldValue);
        Assert.Equal("SAFE", resolution.NewValue);

        // Cleared, which is the mirror image: an oldValue and no newValue.
        var cleared = changelog[2].Changes.Single(change => change.Field == "resolution");
        Assert.Equal("SAFE", cleared.OldValue);
        Assert.Null(cleared.NewValue);
    }

    /// <summary>
    /// <c>hotspots/show</c> returns <c>component</c> and <c>project</c> as objects rather than
    /// strings, so the file comes from the object's own path and the link from the project object.
    /// </summary>
    [Fact]
    public void TheShowEndpointsObjectShapedComponentIsReadAsAPathAndALink()
    {
        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var detail = ResultMapper.Hotspot(show, BaseUrl);

        Assert.Equal("src/Quartz.Examples.AspNetCore/appsettings.json", detail.File);
        Assert.Equal(
            "https://sonarcloud.io/security_hotspots?id=quartznet_quartznet&hotspots=AZvu8ZyfNsnCVHe5poFs",
            detail.Url);
    }

    // ---------------------------------------------------------------------------------------
    // Rule names, write results and the paging envelope
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RuleNamesAreJoinedFromTheSidecarAndAbsentWhenItIsNot()
    {
        var withSidecar = Issues("issues-search-single.json");
        var withoutSidecar = JsonSerializer.Deserialize(
            ToolPayloads.IssuesWithDuplicateComponents,
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var named = ResultMapper.Issues(withSidecar, default, 1, 1, BaseUrl).Issues[0];
        var unnamed = ResultMapper.Issues(withoutSidecar with { Rules = null }, default, 1, 2, BaseUrl).Issues[0];

        Assert.Equal(
            "Null-forgiving operators should not be used when nullable warnings are disabled",
            named.RuleName);

        Assert.Null(unnamed.RuleName);
        Assert.Equal("csharpsquid:S1192", unnamed.Rule);
    }

    /// <summary>
    /// The transitions the caller is told about are the ones read back after the change, not the
    /// ones the operation response happened to echo.
    /// </summary>
    [Fact]
    public void ATransitionResultPrefersTheTransitionsItWasHandedOverTheEchoedOnes()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-do_transition.json"),
            SonarWireJsonContext.Default.IssueOperationResponseDto)!;

        var withReRead = ResultMapper.Transition(response, ["confirm"], BaseUrl);
        var withoutReRead = ResultMapper.Transition(response, availableTransitions: null, BaseUrl);

        Assert.Equal(["confirm"], withReRead.AvailableTransitions);
        Assert.Equal(["reopen"], withoutReRead.AvailableTransitions);
        Assert.Equal("src/Quartz.HttpClient/QuartzHttpClientServiceCollectionExtensions.cs", withReRead.File);
    }

    /// <summary>
    /// A comment response that carries no comments still reports the text that was posted: it is
    /// the one thing the caller knows for certain went through.
    /// </summary>
    [Fact]
    public void ACommentResultFallsBackToTheTextThatWasPosted()
    {
        var response = new IssueOperationResponseDto();

        var result = ResultMapper.Comment(response, "the posted text", BaseUrl);

        Assert.Equal("the posted text", result.Text);
        Assert.Null(result.CommentKey);
    }

    /// <summary>
    /// <c>add_comment</c> answers with every comment on the issue, not with the one just posted, so
    /// the mapper has to pick. On the live capture — two comments, posted three seconds apart — the
    /// newest is the second.
    /// </summary>
    [Fact]
    public void TheCommentReportedBackIsTheNewestOneOnTheIssue()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-add_comment.json"),
            SonarWireJsonContext.Default.IssueOperationResponseDto)!;

        var result = ResultMapper.Comment(response, "ignored", BaseUrl);

        Assert.Equal(2, response.Issue!.Comments!.Count);
        Assert.Equal("AaAoPByOjn1jm7-NQYn-", result.CommentKey);
        Assert.Equal("sonarqube-mcp wire capture 2 - removing shortly", result.Text);
    }

    /// <summary>
    /// And it picks by timestamp, not by position. SonarQube happens to answer in ascending order,
    /// which is exactly why this cannot be tested with a capture: the payload below is the same two
    /// comments listed the other way round, and the answer must not change.
    /// </summary>
    [Fact]
    public void TheNewestCommentIsChosenByTimestampRatherThanByPosition()
    {
        var response = JsonSerializer.Deserialize(
            """
            {
              "issue": {
                "key": "AaAmMrFelhOWn70x31R7",
                "comments": [
                  { "key": "NEWER", "markdown": "second", "createdAt": "2026-08-22T09:50:23+0000" },
                  { "key": "OLDER", "markdown": "first", "createdAt": "2026-08-22T09:50:20+0000" }
                ]
              }
            }
            """,
            SonarWireJsonContext.Default.IssueOperationResponseDto)!;

        var result = ResultMapper.Comment(response, "ignored", BaseUrl);

        Assert.Equal("NEWER", result.CommentKey);
        Assert.Equal("second", result.Text);
    }

    /// <summary>
    /// <c>hasMore</c> needs both halves: an empty page is the last one whatever the total says, and
    /// a total SonarQube did not report is not one this server may invent.
    /// </summary>
    [Fact]
    public void HasMoreIsFalseWhenThePageIsEmptyOrTheTotalIsUnknown()
    {
        var empty = Issues("issues-search-empty.json");

        var emptyResult = ResultMapper.Issues(empty, default, 1, 2, BaseUrl);

        Assert.False(emptyResult.HasMore);
        Assert.Equal(0, emptyResult.TotalCount);

        var noTotal = JsonSerializer.Deserialize(
            """{"issues":[{"key":"K","component":"p:a.cs","project":"p"}]}""",
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var noTotalResult = ResultMapper.Issues(noTotal, default, 1, 50, BaseUrl);

        Assert.False(noTotalResult.HasMore);
        Assert.Null(noTotalResult.TotalCount);
    }

    /// <summary>
    /// A result set at the API's hard ceiling carries the warning before the caller pages into it,
    /// because the failure it prevents is a 400 three pages later.
    /// </summary>
    [Fact]
    public void AResultSetAtTheWindowLimitCarriesTheWarning()
    {
        var response = JsonSerializer.Deserialize(
            """{"total":10000,"p":1,"ps":1,"issues":[{"key":"K","component":"p:a.cs","project":"p"}]}""",
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var result = ResultMapper.Issues(response, default, 1, 1, BaseUrl);

        Assert.NotNull(result.Note);
        Assert.Contains("only lets the first 10000 results be paged through", result.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A branch line whose <c>coveredConditions</c> is missing is partially covered, not covered.
    /// </summary>
    /// <remarks>
    /// Written by hand because SonarQube has never been seen to send it: 39 branch lines across five
    /// live files all carried <c>conditions</c> and <c>coveredConditions</c> together. The guard is
    /// here anyway because the comparison it replaces is a <em>lifted</em> one — <c>null &lt; 2</c>
    /// is false — so the absence would have read as "every branch covered" and dropped the line from
    /// the result entirely. Silent, and wrong in the direction that costs a test. The second line
    /// pins the other half: zero hits is uncovered whatever the conditions say, and uncovered only.
    /// </remarks>
    [Fact]
    public void ABranchLineWithNoCoveredConditionCountIsPartialRatherThanCovered()
    {
        var response = JsonSerializer.Deserialize(
            """
            {
              "sources": [
                { "line": 10, "lineHits": 4, "conditions": 2 },
                { "line": 11, "lineHits": 0, "conditions": 2 }
              ]
            }
            """,
            SonarWireJsonContext.Default.SourcesLinesResponseDto)!;

        var result = ResultMapper.Coverage(response, FileKey, Project, 10, 11, true, default, BaseUrl);

        Assert.Equal([10], result.PartiallyCoveredLines);
        Assert.Equal([11], result.UncoveredLines);

        // Both are worth a test, so both survive the onlyUncovered filter.
        Assert.Equal([10, 11], result.Lines.Select(line => line.Line));
    }

    private static IssuesSearchResponseDto Issues(string fixture) =>
        JsonSerializer.Deserialize(
            SonarFixtures.Read(fixture),
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;
}
