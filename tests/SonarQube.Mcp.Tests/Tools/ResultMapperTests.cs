using System.Text.Json;

using NUnit.Framework;

using SonarQube.Mcp.Http.Models;
using SonarQube.Mcp.Tests.Http;
using SonarQube.Mcp.Tools;
using SonarQube.Mcp.Tools.Models;

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
    [Test]
    public void EveryIssueOnALivePageGetsItsPathFromTheSidecar()
    {
        var response = Issues("issues-search-page.json");

        var result = ResultMapper.Issues(response, default, 1, 2, BaseUrl);

        Assert.That(result.Issues, Is.Not.Empty);

        foreach (var issue in result.Issues)
        {
            Assert.That(string.IsNullOrEmpty(issue.File), Is.False);
            Assert.That(issue.File!, Does.Not.Contain(':'));
        }

        // The key is still there when the caller needs one back.
        foreach (var issue in result.Issues)
        {
            Assert.That(issue.Component!, Does.Contain(':'));
        }
    }

    /// <summary>
    /// The SCM author reaches the single-issue result; an anonymous response has none, so it comes
    /// from the DTO rather than from a fixture.
    /// </summary>
    [Test]
    public void AnIssuesScmAuthorIsReportedOnTheDetailNotAsTheAssignee()
    {
        var issue = new IssueDto { Key = "k", Author = "ada@example.com", Assignee = "bob@github" };

        var detail = ResultMapper.Detail(
            issue, new Dictionary<string, string>(), new Dictionary<string, string>(), default, BaseUrl);

        Assert.That(detail.Author, Is.EqualTo("ada@example.com"));
        Assert.That(detail.Assignee, Is.EqualTo("bob@github"));
    }

    /// <summary>
    /// The sidecar is SonarQube's, not ours. A response that listed the same component twice would
    /// take down a whole issue search over a detail no caller can see.
    /// </summary>
    [Test]
    public void ADuplicateComponentKeyInTheSidecarDoesNotThrow()
    {
        var response = JsonSerializer.Deserialize(
            ToolPayloads.IssuesWithDuplicateComponents,
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var result = ResultMapper.Issues(response, default, 1, 2, BaseUrl);

        Assert.That(result.Issues.Count, Is.EqualTo(2));
        Assert.That(result.Issues[0].File, Is.EqualTo("src/Quartz/Widget.cs"));

        // The second issue's component is not in the sidecar at all, so it falls back.
        Assert.That(result.Issues[1].File, Is.EqualTo("src/Quartz/Other.cs"));
    }

    /// <summary>
    /// With no sidecar the path is the substring after the first colon, which is what a component
    /// key is made of — right for every file component.
    /// </summary>
    [Test]
    public void WithNoSidecarThePathIsTheSubstringAfterTheFirstColon()
    {
        Assert.That(
            ComponentKeys.PathFor(FileKey, ComponentKeys.NoComponents),
            Is.EqualTo("src/Quartz/Core/QuartzScheduler.cs"));
    }

    /// <summary>A project component's key has no path part, so the key itself is the honest answer.</summary>
    [Test]
    public void AKeyWithNoPathPartResolvesToItself()
    {
        Assert.That(ComponentKeys.PathFor(Project, ComponentKeys.NoComponents), Is.EqualTo(Project));
        Assert.That(ComponentKeys.PathFor("proj:", ComponentKeys.NoComponents), Is.EqualTo("proj:"));
        Assert.That(ComponentKeys.PathFor(null, ComponentKeys.NoComponents), Is.EqualTo(string.Empty));
    }

    /// <summary>
    /// A project entry in the sidecar has no <c>path</c>; falling back to its name is what keeps a
    /// project-level issue from reporting an empty file.
    /// </summary>
    [Test]
    public void AComponentWithNoPathFallsBackToItsLongNameThenItsName()
    {
        var map = ComponentKeys.BuildPathMap(
        [
            new ComponentDto { Key = "a", LongName = "the/long/name" },
            new ComponentDto { Key = "b", Name = "the-name" },
            new ComponentDto { Key = "c" },
            new ComponentDto { Path = "orphan/without/a/key.cs" },
        ]);

        Assert.That(map["a"], Is.EqualTo("the/long/name"));
        Assert.That(map["b"], Is.EqualTo("the-name"));
        Assert.That(map.ContainsKey("c"), Is.False);
        Assert.That(map.Count, Is.EqualTo(2));
    }

    [Test]
    public void AnAbsentSidecarProducesTheSharedEmptyMap()
    {
        Assert.That(ComponentKeys.BuildPathMap(null), Is.SameAs(ComponentKeys.NoComponents));
        Assert.That(ComponentKeys.BuildPathMap([]), Is.SameAs(ComponentKeys.NoComponents));
    }

    // ---------------------------------------------------------------------------------------
    // Deep links
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// SonarQube returns no web URL for anything, so every link is composed — and a link that
    /// forgets the scope opens the main branch, where the issue being linked to may not exist.
    /// </summary>
    [Test]
    public void AnIssueLinkCarriesTheBranchTheSearchWasScopedTo()
    {
        var response = Issues("issues-search-single.json");

        var result = ResultMapper.Issues(response, new AnalysisScope("release/4.0", null), 1, 1, BaseUrl);

        Assert.That(
            result.Issues[0].Url,
            Is.EqualTo("https://sonarcloud.io/project/issues?id=quartznet_quartznet" +
            "&issues=AZ_xePOumT_q4T_1FWf8&open=AZ_xePOumT_q4T_1FWf8&branch=release%2F4.0"));
    }

    [Test]
    public void AnIssueLinkCarriesThePullRequestInstead()
    {
        var response = Issues("issues-search-single.json");

        var result = ResultMapper.Issues(response, new AnalysisScope(null, "3266"), 1, 1, BaseUrl);

        Assert.That(result.Issues[0].Url!, Does.EndWith("&pullRequest=3266").Using(StringComparison.Ordinal));
        Assert.That(result.Issues[0].Url!, !Contains.Substring("branch=").Using(StringComparison.Ordinal));
    }

    /// <summary>A component key carries a colon and slashes, none of which may travel raw.</summary>
    [Test]
    public void ComponentKeysAreEscapedExactlyOnceInALink()
    {
        var url = ComponentKeys.ComponentUrl(BaseUrl, Project, FileKey, default);

        Assert.That(
            url,
            Is.EqualTo("https://sonarcloud.io/code?id=quartznet_quartznet" +
            "&selected=quartznet_quartznet%3Asrc%2FQuartz%2FCore%2FQuartzScheduler.cs"));
    }

    [Test]
    public void AHotspotLinkNamesTheProjectAndTheHotspot()
    {
        Assert.That(
            ComponentKeys.HotspotUrl(BaseUrl, Project, "AZvu8Zyf", new AnalysisScope(null, "3266")),
            Is.EqualTo("https://sonarcloud.io/security_hotspots?id=quartznet_quartznet&hotspots=AZvu8Zyf&pullRequest=3266"));
    }

    [Test]
    public void ALinkToNothingInParticularIsStillAValidProjectLink()
    {
        Assert.That(
            ComponentKeys.IssueUrl(BaseUrl, Project, null, default),
            Is.EqualTo("https://sonarcloud.io/project/issues?id=quartznet_quartznet"));

        Assert.That(
            ComponentKeys.ComponentUrl(BaseUrl, Project, null, default),
            Is.EqualTo("https://sonarcloud.io/code?id=quartznet_quartznet"));
    }

    // ---------------------------------------------------------------------------------------
    // The hotspot shapes: one field name, two meanings (C7) and one singular array (C8)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// C7: the search endpoint's <c>assignee</c> is an internal UUID and the show endpoint's is a
    /// login. Same name, different meaning — so the result records must not share one.
    /// </summary>
    [Test]
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

        Assert.That(summary.AssigneeId, Is.EqualTo("AYgE5F7pEoXHSow6lKjD"));
        Assert.That(detail.Assignee, Is.EqualTo("lahma@github"));

        // The same hotspot, so the difference is the endpoint's and not the entity's.
        Assert.That(detail.Key, Is.EqualTo(summary.Key));
    }

    /// <summary>
    /// C8: the array is spelled <c>comment</c>, singular. The obvious plural deserialises to
    /// <see langword="null"/> with no error, which is the worst possible failure mode — so the
    /// payload below carries both and only the singular one may be read.
    /// </summary>
    [Test]
    public void HotspotReviewCommentsAreReadFromTheSingularProperty()
    {
        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show-with-comment.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var detail = ResultMapper.Hotspot(show, BaseUrl);

        Assert.That(detail.Comments, Has.Exactly(1).Items);
        var comment = detail.Comments.Single();

        Assert.That(comment.Key, Is.EqualTo("AaAoPgsRCRl6zX9b36TK"));
        Assert.That(comment.Text, Is.EqualTo("sonarqube-mcp verification - restored to original state"));
        Assert.That(comment.Author, Is.EqualTo("lahma@github"));
    }

    /// <summary>
    /// The other half of C8, which no capture can carry: SonarQube never sends the plural spelling,
    /// so the only way to prove it is not read is to send both and watch the plural be ignored.
    /// </summary>
    [Test]
    public void APluralCommentsPropertyOnAHotspotIsIgnored()
    {
        var show = JsonSerializer.Deserialize(
            ToolPayloads.HotspotShowWithBothCommentSpellings,
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var detail = ResultMapper.Hotspot(show, BaseUrl);

        Assert.That(detail.Comments, Has.Exactly(1).Items);
        Assert.That(detail.Comments.Single().Text, Is.EqualTo("the singular property"));
        Assert.That(detail.Comments, Has.None.Matches<HotspotComment>(entry => entry!.Key == "ignored"));
    }

    /// <summary>
    /// A live changelog, which is richer than the obvious shape in two ways: a status change carries
    /// <em>two</em> diffs (status and resolution move together), and a diff that clears a value has
    /// an <c>oldValue</c> with no <c>newValue</c>. The first entry is SonarQube's own severity
    /// recalculation and has no user at all.
    /// </summary>
    [Test]
    public void AHotspotsReviewHistoryIsMappedFieldByField()
    {
        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show-with-comment.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var changelog = ResultMapper.Hotspot(show, BaseUrl).Changelog;

        var automatic = changelog[0];
        Assert.That(automatic.User, Is.Null);
        Assert.That(automatic.UserName, Is.Null);
        Assert.That(automatic.Changes, Has.Exactly(1).Items);
        Assert.That(automatic.Changes.Single().Field, Is.EqualTo("severity"));

        var review = changelog[1];
        Assert.That(review.User, Is.EqualTo("lahma@github"));
        Assert.That(review.UserName, Is.EqualTo("Marko Lahma"));

        var status = review.Changes.Single(change => change.Field == "status");
        Assert.That(status.OldValue, Is.EqualTo("TO_REVIEW"));
        Assert.That(status.NewValue, Is.EqualTo("REVIEWED"));

        // Set, so there is a newValue and nothing before it.
        var resolution = review.Changes.Single(change => change.Field == "resolution");
        Assert.That(resolution.OldValue, Is.Null);
        Assert.That(resolution.NewValue, Is.EqualTo("SAFE"));

        // Cleared, which is the mirror image: an oldValue and no newValue.
        var cleared = changelog[2].Changes.Single(change => change.Field == "resolution");
        Assert.That(cleared.OldValue, Is.EqualTo("SAFE"));
        Assert.That(cleared.NewValue, Is.Null);
    }

    /// <summary>
    /// <c>hotspots/show</c> returns <c>component</c> and <c>project</c> as objects rather than
    /// strings, so the file comes from the object's own path and the link from the project object.
    /// </summary>
    [Test]
    public void TheShowEndpointsObjectShapedComponentIsReadAsAPathAndALink()
    {
        var show = JsonSerializer.Deserialize(
            SonarFixtures.Read("hotspots-show.json"),
            SonarWireJsonContext.Default.HotspotShowResponseDto)!;

        var detail = ResultMapper.Hotspot(show, BaseUrl);

        Assert.That(detail.File, Is.EqualTo("src/Quartz.Examples.AspNetCore/appsettings.json"));
        Assert.That(
            detail.Url,
            Is.EqualTo("https://sonarcloud.io/security_hotspots?id=quartznet_quartznet&hotspots=AZvu8ZyfNsnCVHe5poFs"));
    }

    // ---------------------------------------------------------------------------------------
    // Rule names, write results and the paging envelope
    // ---------------------------------------------------------------------------------------

    [Test]
    public void RuleNamesAreJoinedFromTheSidecarAndAbsentWhenItIsNot()
    {
        var withSidecar = Issues("issues-search-single.json");
        var withoutSidecar = JsonSerializer.Deserialize(
            ToolPayloads.IssuesWithDuplicateComponents,
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var named = ResultMapper.Issues(withSidecar, default, 1, 1, BaseUrl).Issues[0];
        var unnamed = ResultMapper.Issues(withoutSidecar with { Rules = null }, default, 1, 2, BaseUrl).Issues[0];

        Assert.That(
            named.RuleName,
            Is.EqualTo("Null-forgiving operators should not be used when nullable warnings are disabled"));

        Assert.That(unnamed.RuleName, Is.Null);
        Assert.That(unnamed.Rule, Is.EqualTo("csharpsquid:S1192"));
    }

    /// <summary>
    /// The transitions the caller is told about are the ones read back after the change, not the
    /// ones the operation response happened to echo.
    /// </summary>
    [Test]
    public void ATransitionResultPrefersTheTransitionsItWasHandedOverTheEchoedOnes()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-do_transition.json"),
            SonarWireJsonContext.Default.IssueOperationResponseDto)!;

        var withReRead = ResultMapper.Transition(response, ["confirm"], BaseUrl);
        var withoutReRead = ResultMapper.Transition(response, availableTransitions: null, BaseUrl);

        Assert.That(withReRead.AvailableTransitions, Is.EqualTo<string[]>(["confirm"]));
        Assert.That(withoutReRead.AvailableTransitions, Is.EqualTo<string[]>(["reopen"]));
        Assert.That(withReRead.File, Is.EqualTo("src/Quartz.HttpClient/QuartzHttpClientServiceCollectionExtensions.cs"));
    }

    /// <summary>
    /// A comment response that carries no comments still reports the text that was posted: it is
    /// the one thing the caller knows for certain went through.
    /// </summary>
    [Test]
    public void ACommentResultFallsBackToTheTextThatWasPosted()
    {
        var response = new IssueOperationResponseDto();

        var result = ResultMapper.Comment(response, "the posted text", BaseUrl);

        Assert.That(result.Text, Is.EqualTo("the posted text"));
        Assert.That(result.CommentKey, Is.Null);
    }

    /// <summary>
    /// <c>add_comment</c> answers with every comment on the issue, not with the one just posted, so
    /// the mapper has to pick. On the live capture — two comments, posted three seconds apart — the
    /// newest is the second.
    /// </summary>
    [Test]
    public void TheCommentReportedBackIsTheNewestOneOnTheIssue()
    {
        var response = JsonSerializer.Deserialize(
            SonarFixtures.Read("issues-add_comment.json"),
            SonarWireJsonContext.Default.IssueOperationResponseDto)!;

        var result = ResultMapper.Comment(response, "ignored", BaseUrl);

        Assert.That(response.Issue!.Comments!.Count, Is.EqualTo(2));
        Assert.That(result.CommentKey, Is.EqualTo("AaAoPByOjn1jm7-NQYn-"));
        Assert.That(result.Text, Is.EqualTo("sonarqube-mcp wire capture 2 - removing shortly"));
    }

    /// <summary>
    /// And it picks by timestamp, not by position. SonarQube happens to answer in ascending order,
    /// which is exactly why this cannot be tested with a capture: the payload below is the same two
    /// comments listed the other way round, and the answer must not change.
    /// </summary>
    [Test]
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

        Assert.That(result.CommentKey, Is.EqualTo("NEWER"));
        Assert.That(result.Text, Is.EqualTo("second"));
    }

    /// <summary>
    /// <c>hasMore</c> needs both halves: an empty page is the last one whatever the total says, and
    /// a total SonarQube did not report is not one this server may invent.
    /// </summary>
    [Test]
    public void HasMoreIsFalseWhenThePageIsEmptyOrTheTotalIsUnknown()
    {
        var empty = Issues("issues-search-empty.json");

        var emptyResult = ResultMapper.Issues(empty, default, 1, 2, BaseUrl);

        Assert.That(emptyResult.HasMore, Is.False);
        Assert.That(emptyResult.TotalCount, Is.EqualTo(0));

        var noTotal = JsonSerializer.Deserialize(
            """{"issues":[{"key":"K","component":"p:a.cs","project":"p"}]}""",
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var noTotalResult = ResultMapper.Issues(noTotal, default, 1, 50, BaseUrl);

        Assert.That(noTotalResult.HasMore, Is.False);
        Assert.That(noTotalResult.TotalCount, Is.Null);
    }

    /// <summary>
    /// A result set at the API's hard ceiling carries the warning before the caller pages into it,
    /// because the failure it prevents is a 400 three pages later.
    /// </summary>
    [Test]
    public void AResultSetAtTheWindowLimitCarriesTheWarning()
    {
        var response = JsonSerializer.Deserialize(
            """{"total":10000,"p":1,"ps":1,"issues":[{"key":"K","component":"p:a.cs","project":"p"}]}""",
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;

        var result = ResultMapper.Issues(response, default, 1, 1, BaseUrl);

        Assert.That(result.Note, Is.Not.Null);
        Assert.That(result.Note, Contains.Substring("only lets the first 10000 results be paged through").Using(StringComparison.Ordinal));
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
    [Test]
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

        Assert.That(result.PartiallyCoveredLines, Is.EqualTo<int[]>([10]));
        Assert.That(result.UncoveredLines, Is.EqualTo<int[]>([11]));

        // Both are worth a test, so both survive the onlyUncovered filter.
        Assert.That(result.Lines.Select(line => line.Line), Is.EqualTo<int[]>([10, 11]));
    }

    private static IssuesSearchResponseDto Issues(string fixture) =>
        JsonSerializer.Deserialize(
            SonarFixtures.Read(fixture),
            SonarWireJsonContext.Default.IssuesSearchResponseDto)!;
}
