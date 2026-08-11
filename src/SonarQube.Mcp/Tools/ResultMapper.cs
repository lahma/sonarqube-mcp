using System.Globalization;

using SonarQube.Mcp.Http;
using SonarQube.Mcp.Http.Models;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// Turns SonarQube's wire shapes into the tool results.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is where the context budget is actually won. A SonarQube issue carries the same
/// severity twice in two vocabularies, a component key that repeats the project key on every row, a
/// line hash, an SCM author, a debt estimate under two names and — for a data-flow rule — a dozen
/// flows of secondary locations. What survives is what somebody triaging the issue would ask for.
/// </para>
/// <para>
/// It is also the boundary that keeps the wire DTOs out of the tool schemas: the result records are
/// camelCase and ours, the DTOs carry explicit property names and are SonarQube's, and no type
/// serialises through both contexts.
/// </para>
/// </remarks>
internal static class ResultMapper
{
    /// <summary>The lookup for a response that carried no <c>rules[]</c> sidecar.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoRuleNames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // -----------------------------------------------------------------------------------------
    // Projects, components, branches, pull requests, quality gates
    // -----------------------------------------------------------------------------------------

    /// <summary>Maps one page of projects.</summary>
    internal static ProjectListResult Projects(
        ComponentsSearchResponseDto response,
        int requestedPage,
        int requestedPageSize,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = PagedResult.From(response, response.Components, requestedPage, requestedPageSize);
        var projects = new List<ProjectSummary>(page.Items.Count);

        foreach (var component in page.Items)
        {
            projects.Add(new ProjectSummary
            {
                Key = component.Key,
                Name = component.Name,
                Qualifier = component.Qualifier,
                Url = ComponentKeys.ProjectUrl(baseUrl, component.Key, default),
            });
        }

        return new ProjectListResult
        {
            Projects = projects,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = page.Total,
            HasMore = HasMore(page),
            Note = CapNote(page.Total),
        };
    }

    /// <summary>Maps one page of components.</summary>
    internal static ComponentListResult Components(
        ComponentsTreeResponseDto response,
        string project,
        AnalysisScope scope,
        int requestedPage,
        int requestedPageSize,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = PagedResult.From(response, response.Components, requestedPage, requestedPageSize);
        var components = new List<ComponentSummary>(page.Items.Count);

        foreach (var component in page.Items)
        {
            components.Add(new ComponentSummary
            {
                Component = component.Key,
                Path = component.Path ?? component.LongName,
                Name = component.Name,
                Qualifier = component.Qualifier,
                Language = component.Language,
                Url = ComponentKeys.ComponentUrl(baseUrl, project, component.Key, scope),
            });
        }

        return new ComponentListResult
        {
            Components = components,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = page.Total,
            HasMore = HasMore(page),
            Note = CapNote(page.Total),
        };
    }

    /// <summary>Maps the branch list. This endpoint has no paging at all.</summary>
    internal static BranchListResult Branches(BranchesListResponseDto response, string project, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var branches = new List<BranchSummary>(response.Branches?.Count ?? 0);

        foreach (var branch in response.Branches ?? [])
        {
            branches.Add(new BranchSummary
            {
                Name = branch.Name,
                IsMain = branch.IsMain ?? false,
                Type = branch.Type,
                AnalysisDate = branch.AnalysisDate,
                Bugs = branch.Status?.Bugs,
                Vulnerabilities = branch.Status?.Vulnerabilities,
                CodeSmells = branch.Status?.CodeSmells,
                QualityGateStatus = branch.Status?.QualityGateStatus,
                CommitSha = branch.Commit?.Sha,
                CommitMessage = branch.Commit?.Message,
                Url = ComponentKeys.ProjectUrl(baseUrl, project, new AnalysisScope(branch.Name, null)),
            });
        }

        return new BranchListResult { Branches = branches };
    }

    /// <summary>Maps the pull request list. This endpoint has no paging at all.</summary>
    internal static PullRequestListResult PullRequests(
        PullRequestsListResponseDto response,
        string project,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var pullRequests = new List<PullRequestSummary>(response.PullRequests?.Count ?? 0);

        foreach (var pullRequest in response.PullRequests ?? [])
        {
            pullRequests.Add(new PullRequestSummary
            {
                Key = pullRequest.Key,
                Title = pullRequest.Title,
                Branch = pullRequest.Branch,
                Base = pullRequest.Base ?? pullRequest.Target,
                QualityGateStatus = pullRequest.Status?.QualityGateStatus,
                Bugs = pullRequest.Status?.Bugs,
                Vulnerabilities = pullRequest.Status?.Vulnerabilities,
                CodeSmells = pullRequest.Status?.CodeSmells,
                AnalysisDate = pullRequest.AnalysisDate,
                ScmUrl = pullRequest.Url,
                Url = ComponentKeys.ProjectUrl(baseUrl, project, new AnalysisScope(null, pullRequest.Key)),
            });
        }

        return new PullRequestListResult { PullRequests = pullRequests };
    }

    /// <summary>
    /// Maps a quality gate verdict, pre-filtering the failing conditions and explaining
    /// <c>NONE</c>.
    /// </summary>
    internal static QualityGateResult QualityGate(
        ProjectStatusResponseDto response,
        string project,
        AnalysisScope scope,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = response.ProjectStatus;
        var conditions = new List<QualityGateCondition>(status?.Conditions?.Count ?? 0);
        var failing = new List<QualityGateCondition>();

        foreach (var condition in status?.Conditions ?? [])
        {
            var mapped = new QualityGateCondition
            {
                Metric = condition.MetricKey,
                Status = condition.Status,
                Comparator = condition.Comparator,
                Threshold = MeasureFormatting.Format(condition.MetricKey, condition.ErrorThreshold),
                ActualValue = MeasureFormatting.Format(condition.MetricKey, condition.ActualValue),
                OnNewCode = condition.PeriodIndex == 1,
            };

            conditions.Add(mapped);

            if (string.Equals(condition.Status, "ERROR", StringComparison.Ordinal))
            {
                failing.Add(mapped);
            }
        }

        // "NONE" with no conditions is the normal answer for a scope that has never been measured
        // against a gate. Left unexplained it reads as a failure, and the next thing a model does is
        // retry the call.
        var note = string.Equals(status?.Status, "NONE", StringComparison.Ordinal) && conditions.Count == 0
            ? "No quality gate has been computed for this scope yet — this is normal for a project or pull " +
              "request that has not been analysed against a gate, not an error."
            : null;

        return new QualityGateResult
        {
            ProjectKey = project,
            Branch = scope.Branch,
            PullRequest = scope.PullRequest,
            Status = status?.Status,
            Conditions = conditions,
            FailingConditions = failing,
            Url = ComponentKeys.ProjectUrl(baseUrl, project, scope),
            Note = note,
        };
    }

    // -----------------------------------------------------------------------------------------
    // Issues, rules and hotspots
    // -----------------------------------------------------------------------------------------

    /// <summary>Maps one page of issues, joining component keys to paths and rule keys to names.</summary>
    internal static IssueSearchResult Issues(
        IssuesSearchResponseDto response,
        AnalysisScope scope,
        int requestedPage,
        int requestedPageSize,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = PagedResult.From(response, response.Issues, requestedPage, requestedPageSize);
        var paths = ComponentKeys.BuildPathMap(response.Components);
        var ruleNames = RuleNames(response.Rules);

        var issues = new List<IssueSummary>(page.Items.Count);

        foreach (var issue in page.Items)
        {
            issues.Add(Issue(issue, paths, ruleNames, scope, baseUrl));
        }

        return new IssueSearchResult
        {
            Issues = issues,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = page.Total,
            HasMore = HasMore(page),
            Note = CapNote(page.Total),
        };
    }

    /// <summary>Maps one issue as a list entry.</summary>
    internal static IssueSummary Issue(
        IssueDto issue,
        IReadOnlyDictionary<string, string> paths,
        IReadOnlyDictionary<string, string> ruleNames,
        AnalysisScope scope,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(ruleNames);

        return new IssueSummary
        {
            Key = issue.Key,
            Rule = issue.Rule,
            RuleName = RuleName(issue.Rule, ruleNames),
            Message = issue.Message,
            File = ComponentKeys.PathFor(issue.Component, paths),
            Line = issue.Line ?? issue.TextRange?.StartLine,
            EndLine = EndLine(issue),
            Component = issue.Component,
            Project = issue.Project,
            IssueStatus = issue.IssueStatus,
            Impacts = Impacts(issue.Impacts),
            Severity = issue.Severity,
            Type = issue.Type,
            CleanCodeAttribute = issue.CleanCodeAttribute,
            CleanCodeAttributeCategory = issue.CleanCodeAttributeCategory,
            Tags = issue.Tags ?? [],
            Assignee = issue.Assignee,
            Effort = issue.Effort ?? issue.Debt,
            CreationDate = issue.CreationDate,
            UpdateDate = issue.UpdateDate,
            Url = ComponentKeys.IssueUrl(baseUrl, issue.Project, issue.Key, scope),
        };
    }

    /// <summary>Maps one issue in full, including the transitions that are legal right now.</summary>
    internal static IssueDetail Detail(
        IssueDto issue,
        IReadOnlyDictionary<string, string> paths,
        IReadOnlyDictionary<string, string> ruleNames,
        AnalysisScope scope,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(ruleNames);

        return new IssueDetail
        {
            Key = issue.Key,
            Rule = issue.Rule,
            RuleName = RuleName(issue.Rule, ruleNames),
            Message = issue.Message,
            File = ComponentKeys.PathFor(issue.Component, paths),
            Line = issue.Line ?? issue.TextRange?.StartLine,
            EndLine = EndLine(issue),
            Component = issue.Component,
            Project = issue.Project,
            IssueStatus = issue.IssueStatus,
            Impacts = Impacts(issue.Impacts),
            Severity = issue.Severity,
            Type = issue.Type,
            CleanCodeAttribute = issue.CleanCodeAttribute,
            CleanCodeAttributeCategory = issue.CleanCodeAttributeCategory,
            Tags = issue.Tags ?? [],
            Assignee = issue.Assignee,
            Effort = issue.Effort ?? issue.Debt,
            CreationDate = issue.CreationDate,
            UpdateDate = issue.UpdateDate,
            AvailableTransitions = issue.Transitions ?? [],
            Comments = Comments(issue.Comments),
            Flows = Flows(issue.Flows, paths),
            TextRange = TextRange(issue.TextRange),
            Url = ComponentKeys.IssueUrl(baseUrl, issue.Project, issue.Key, scope),
        };
    }

    /// <summary>
    /// Maps a rule, converting the requested description sections from HTML and saying so when
    /// there were none.
    /// </summary>
    /// <remarks>
    /// The section-less case is not a failure: an anonymous request comes back with the rule's name,
    /// impacts and clean-code attribute and no descriptions at all, which looks like an entitlement
    /// restriction. The tool degrades with a note rather than throwing, because the half it did get
    /// is still worth having.
    /// </remarks>
    internal static RuleDetail Rule(RuleDto rule, IReadOnlyList<string> requestedSections, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(requestedSections);

        var sections = new List<RuleSection>();
        var truncated = false;

        // Requested order, not response order: the caller asked for introduction before how_to_fix
        // because that is the order they are read in.
        foreach (var key in requestedSections)
        {
            foreach (var section in rule.DescriptionSections ?? [])
            {
                if (!string.Equals(section.Key, key, StringComparison.Ordinal))
                {
                    continue;
                }

                var (content, cut) = HtmlToText.Convert(section.Content, ToolDefaults.MaxRuleSectionChars);
                truncated |= cut;

                sections.Add(new RuleSection
                {
                    Key = section.Key,
                    Content = content,
                    Context = section.Context?.DisplayName ?? section.Context?.Key,
                });
            }
        }

        // A rule that predates sections has one HTML blob instead. Rendering it as the root_cause
        // section is closer to the truth than reporting no description at all.
        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(rule.HtmlDesc))
        {
            var (content, cut) = HtmlToText.Convert(rule.HtmlDesc, ToolDefaults.MaxRuleSectionChars);
            truncated |= cut;

            sections.Add(new RuleSection { Key = "root_cause", Content = content });
        }

        var note = sections.Count == 0
            ? "SonarQube returned this rule without any description sections. That happens when the request " +
              "is anonymous or the token's account is not entitled to read rule descriptions — set " +
              "SONARQUBE_TOKEN and try again. The rule's name, impacts and clean-code attribute above are " +
              "unaffected."
            : null;

        return new RuleDetail
        {
            Key = rule.Key,
            Name = rule.Name,
            Language = rule.LangName ?? rule.Lang,
            Repository = rule.Repo,
            Type = rule.Type,
            Severity = rule.Severity,
            CleanCodeAttribute = rule.CleanCodeAttribute,
            CleanCodeAttributeCategory = rule.CleanCodeAttributeCategory,
            Impacts = Impacts(rule.Impacts),
            Tags = rule.Tags ?? rule.SysTags ?? [],
            SecurityStandards = rule.SecurityStandards ?? [],
            Sections = sections,
            Truncated = truncated,
            Url = RuleUrl(baseUrl, rule.Key),
            Note = note,
        };
    }

    /// <summary>Maps one page of security hotspots.</summary>
    internal static HotspotSearchResult Hotspots(
        HotspotsSearchResponseDto response,
        string project,
        AnalysisScope scope,
        int requestedPage,
        int requestedPageSize,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = PagedResult.From(response, response.Hotspots, requestedPage, requestedPageSize);
        var paths = ComponentKeys.BuildPathMap(response.Components);

        var hotspots = new List<HotspotSummary>(page.Items.Count);

        foreach (var hotspot in page.Items)
        {
            hotspots.Add(new HotspotSummary
            {
                Key = hotspot.Key,
                File = ComponentKeys.PathFor(hotspot.Component, paths),
                Line = hotspot.Line ?? hotspot.TextRange?.StartLine,
                Message = hotspot.Message,
                SecurityCategory = hotspot.SecurityCategory,
                VulnerabilityProbability = hotspot.VulnerabilityProbability,
                Status = hotspot.Status,
                Resolution = hotspot.Resolution,
                RuleKey = hotspot.RuleKey,
                AssigneeId = hotspot.Assignee,
                CreationDate = hotspot.CreationDate,
                UpdateDate = hotspot.UpdateDate,
                Component = hotspot.Component,
                Url = ComponentKeys.HotspotUrl(baseUrl, hotspot.Project ?? project, hotspot.Key, scope),
            });
        }

        return new HotspotSearchResult
        {
            Hotspots = hotspots,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = page.Total,
            HasMore = HasMore(page),
            Note = CapNote(page.Total),
        };
    }

    /// <summary>Maps one security hotspot in full, converting the rule's three explanations from HTML.</summary>
    internal static HotspotDetail Hotspot(HotspotShowResponseDto response, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var comments = new List<HotspotComment>(response.Comment?.Count ?? 0);

        // The wire property really is "comment", singular — every other collection in this API is
        // plural, and the obvious spelling deserialises to null with no error.
        foreach (var comment in response.Comment ?? [])
        {
            comments.Add(new HotspotComment
            {
                Key = comment.Key,
                Author = comment.Login,
                Text = comment.Markdown ?? comment.HtmlText,
                CreatedAt = comment.CreatedAt,
            });
        }

        var changelog = new List<HotspotChangelogEntry>(response.Changelog?.Count ?? 0);

        foreach (var entry in response.Changelog ?? [])
        {
            var changes = new List<HotspotChange>(entry.Diffs?.Count ?? 0);

            foreach (var diff in entry.Diffs ?? [])
            {
                changes.Add(new HotspotChange
                {
                    Field = diff.Key,
                    OldValue = diff.OldValue,
                    NewValue = diff.NewValue,
                });
            }

            changelog.Add(new HotspotChangelogEntry
            {
                User = entry.User,
                UserName = entry.UserName,
                CreationDate = entry.CreationDate,
                Changes = changes,
            });
        }

        var rule = response.Rule is null ? null : new HotspotRule
        {
            Key = response.Rule.Key,
            Name = response.Rule.Name,
            RiskDescription = Prose(response.Rule.RiskDescription),
            VulnerabilityDescription = Prose(response.Rule.VulnerabilityDescription),
            FixRecommendations = Prose(response.Rule.FixRecommendations),
        };

        return new HotspotDetail
        {
            Key = response.Key,
            File = response.Component?.Path ?? ComponentKeys.PathFor(response.Component?.Key, ComponentKeys.NoComponents),
            Line = response.Line ?? response.TextRange?.StartLine,
            Message = response.Message,
            Status = response.Status,
            Resolution = response.Resolution,
            SecurityCategory = response.Rule?.SecurityCategory,
            VulnerabilityProbability = response.Rule?.VulnerabilityProbability,

            // A login here, unlike the UUID the search endpoint reports under the same name.
            Assignee = response.Assignee,
            Rule = rule,
            Comments = comments,
            Changelog = changelog,
            CanChangeStatus = response.CanChangeStatus,
            Url = ComponentKeys.HotspotUrl(baseUrl, response.Project?.Key, response.Key, default),
        };
    }

    // -----------------------------------------------------------------------------------------
    // Measures, metrics and coverage
    // -----------------------------------------------------------------------------------------

    /// <summary>Maps one component's measures and diffs them against what was asked for.</summary>
    internal static MeasuresResult Measures(
        MeasuresComponentResponseDto response,
        IReadOnlyList<string> requestedMetrics,
        string project,
        AnalysisScope scope,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var component = response.Component;
        var returned = new HashSet<string>(StringComparer.Ordinal);
        MeasureFormatting.CollectMetrics(component?.Measures, returned);

        return new MeasuresResult
        {
            Component = component?.Key,
            Path = component?.Path,
            ProjectKey = component?.Project ?? project,
            Branch = scope.Branch,
            PullRequest = scope.PullRequest,
            Measures = MeasureFormatting.Values(component?.Measures),
            MissingMetrics = MeasureFormatting.MissingMetrics(requestedMetrics, returned),
            Url = ComponentKeys.ComponentUrl(baseUrl, project, component?.Key, scope),
        };
    }

    /// <summary>Maps one page of components with their measures.</summary>
    internal static ComponentMeasuresResult TreeMeasures(
        MeasuresComponentTreeResponseDto response,
        IReadOnlyList<string> requestedMetrics,
        string project,
        AnalysisScope scope,
        int requestedPage,
        int requestedPageSize,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = PagedResult.From(response, response.Components, requestedPage, requestedPageSize);
        var returned = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<ComponentMeasures>(page.Items.Count);

        foreach (var component in page.Items)
        {
            MeasureFormatting.CollectMetrics(component.Measures, returned);

            components.Add(new ComponentMeasures
            {
                Component = component.Key,
                Path = component.Path ?? component.LongName,
                Name = component.Name,
                Qualifier = component.Qualifier,
                Measures = MeasureFormatting.Values(component.Measures),
                Url = ComponentKeys.ComponentUrl(baseUrl, project, component.Key, scope),
            });
        }

        return new ComponentMeasuresResult
        {
            Components = components,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = page.Total,
            HasMore = HasMore(page),
            Note = CapNote(page.Total),
            MissingMetrics = MeasureFormatting.MissingMetrics(requestedMetrics, returned),
        };
    }

    /// <summary>Maps a measure history, saying what its total actually counts.</summary>
    internal static MeasuresHistoryResult History(
        MeasuresHistoryResponseDto response,
        string component,
        int requestedPage,
        int requestedPageSize)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = PagedResult.From(response, response.Measures, requestedPage, requestedPageSize);
        var metrics = new List<MetricHistory>(page.Items.Count);

        foreach (var measure in page.Items)
        {
            var history = new List<MeasureHistoryPoint>(measure.History?.Count ?? 0);

            foreach (var entry in measure.History ?? [])
            {
                history.Add(new MeasureHistoryPoint
                {
                    Date = entry.Date,
                    Value = MeasureFormatting.Format(measure.Metric, entry.Value),
                });
            }

            metrics.Add(new MetricHistory { Metric = measure.Metric, History = history });
        }

        // The paging on this endpoint is over analyses, and every requested metric shares the page.
        // Left unsaid, a totalCount of 29 for two metrics reads as a wrong item count.
        return new MeasuresHistoryResult
        {
            Component = component,
            Metrics = metrics,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = page.Total,
            HasMore = page.Total is { } total && (long) page.Page * page.PageSize < total,
            Note = "totalCount is the number of analyses in the project's history, not the number of measures; " +
                   "each metric's series is paged over the same analyses.",
        };
    }

    /// <summary>
    /// Filters the metric catalogue client-side.
    /// </summary>
    /// <remarks>
    /// The endpoint has no query parameter of its own, and it returns every metric in one call — so
    /// filtering here is both cheaper than paging and more useful, because a substring match over
    /// key, name and description finds <c>duplicated_lines_density</c> from the word "duplication".
    /// <c>DATA</c> and <c>DISTRIB</c> metrics are dropped by default: their values are multi-kilobyte
    /// blobs of per-line data that no model can act on.
    /// </remarks>
    internal static MetricListResult Metrics(
        MetricsSearchResponseDto response,
        string? query,
        string? domain,
        bool includeDataMetrics)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metrics = new List<MetricSummary>();
        var dropped = 0;

        foreach (var metric in response.Metrics ?? [])
        {
            if (string.IsNullOrEmpty(metric.Key))
            {
                continue;
            }

            if (!includeDataMetrics && IsBulkMetric(metric.Type))
            {
                dropped++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(domain)
                && !string.Equals(metric.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query) && !MatchesQuery(metric, query.Trim()))
            {
                continue;
            }

            metrics.Add(new MetricSummary
            {
                Key = metric.Key,
                Name = metric.Name,
                Type = metric.Type,
                Domain = metric.Domain,
                Description = metric.Description,

                // direction, not a boolean the API sends: 1 means higher is better, -1 means lower
                // is, 0 means the metric is neither good nor bad.
                HigherIsBetter = metric.Direction switch
                {
                    > 0 => true,
                    < 0 => false,
                    _ => null,
                },
            });
        }

        var note = dropped > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{dropped} metric(s) of type DATA or DISTRIB were left out because their values are " +
                $"multi-kilobyte per-line blobs; pass includeDataMetrics=true to include them.")
            : null;

        return new MetricListResult
        {
            Metrics = metrics,
            TotalCount = metrics.Count,
            Note = note,
        };
    }

    /// <summary>
    /// Maps a file's per-line coverage facts, dropping the source text.
    /// </summary>
    /// <param name="response">The lines SonarQube returned.</param>
    /// <param name="component">The component key that was read.</param>
    /// <param name="project">The project, for the deep link.</param>
    /// <param name="from">First line requested.</param>
    /// <param name="to">Last line requested.</param>
    /// <param name="onlyUncovered">Whether <c>lines</c> keeps only the lines worth writing a test for.</param>
    /// <param name="scope">The branch or pull request the call was scoped to.</param>
    /// <param name="baseUrl">The configured origin, for the deep link.</param>
    internal static FileCoverageResult Coverage(
        SourcesLinesResponseDto response,
        string component,
        string project,
        int from,
        int to,
        bool onlyUncovered,
        AnalysisScope scope,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var uncovered = new List<int>();
        var partial = new List<int>();
        var newLines = new List<int>();
        var duplicated = new List<int>();
        var lines = new List<FileCoverageLine>();
        var measured = false;

        foreach (var line in response.Sources ?? [])
        {
            if (line.Line is not { } number)
            {
                continue;
            }

            var hits = line.LineHits;
            var conditions = line.Conditions;
            var covered = line.CoveredConditions;

            measured |= hits is not null;

            var isUncovered = hits == 0;
            var isPartial = hits > 0 && conditions > 0 && covered < conditions;

            if (isUncovered)
            {
                uncovered.Add(number);
            }

            if (isPartial)
            {
                partial.Add(number);
            }

            if (line.IsNew == true)
            {
                newLines.Add(number);
            }

            if (line.Duplicated == true)
            {
                duplicated.Add(number);
            }

            // The source text is never returned: it arrives as syntax-highlighted HTML, the agent
            // already has the file, and it would be most of the payload.
            if (!onlyUncovered || isUncovered || isPartial)
            {
                lines.Add(new FileCoverageLine
                {
                    Line = number,
                    Hits = hits,
                    Conditions = conditions,
                    CoveredConditions = covered,
                    IsNew = line.IsNew,
                    Duplicated = line.Duplicated,
                });
            }
        }

        return new FileCoverageResult
        {
            Component = component,
            Path = ComponentKeys.PathFor(component, ComponentKeys.NoComponents),
            From = from,
            To = to,
            UncoveredLines = uncovered,
            PartiallyCoveredLines = partial,
            NewLines = newLines,
            DuplicatedLines = duplicated,
            Lines = lines,
            Note = CoverageNote(measured, onlyUncovered, from, to),
            Url = ComponentKeys.ComponentUrl(baseUrl, project, component, scope),
        };
    }

    // -----------------------------------------------------------------------------------------
    // Write results
    // -----------------------------------------------------------------------------------------

    /// <summary>Maps the result of a transition, with the transitions that are legal afterwards.</summary>
    internal static IssueTransitionResult Transition(
        IssueOperationResponseDto response,
        IReadOnlyList<string>? availableTransitions,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var issue = response.Issue;
        var paths = ComponentKeys.BuildPathMap(response.Components);

        return new IssueTransitionResult
        {
            Key = issue?.Key,
            IssueStatus = issue?.IssueStatus,
            Status = issue?.Status,
            Resolution = issue?.Resolution,
            Message = issue?.Message,
            File = ComponentKeys.PathFor(issue?.Component, paths),
            Line = issue?.Line ?? issue?.TextRange?.StartLine,
            AvailableTransitions = availableTransitions ?? issue?.Transitions ?? [],
            Url = ComponentKeys.IssueUrl(baseUrl, issue?.Project, issue?.Key, default),
        };
    }

    /// <summary>Maps the result of an assignment.</summary>
    internal static IssueAssignResult Assign(IssueOperationResponseDto response, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var issue = response.Issue;
        var paths = ComponentKeys.BuildPathMap(response.Components);

        return new IssueAssignResult
        {
            Key = issue?.Key,
            Assignee = issue?.Assignee,
            Message = issue?.Message,
            File = ComponentKeys.PathFor(issue?.Component, paths),
            Line = issue?.Line ?? issue?.TextRange?.StartLine,
            Url = ComponentKeys.IssueUrl(baseUrl, issue?.Project, issue?.Key, default),
        };
    }

    /// <summary>Maps the result of a comment, picking the newest comment as the one just posted.</summary>
    internal static IssueCommentResult Comment(IssueOperationResponseDto response, string fallbackText, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        var issue = response.Issue;
        var paths = ComponentKeys.BuildPathMap(response.Components);
        var newest = NewestComment(issue?.Comments);

        return new IssueCommentResult
        {
            Key = issue?.Key,
            CommentKey = newest?.Key,
            Text = newest?.Markdown ?? newest?.HtmlText ?? fallbackText,
            CreatedAt = newest?.CreatedAt,
            Message = issue?.Message,
            File = ComponentKeys.PathFor(issue?.Component, paths),
            Url = ComponentKeys.IssueUrl(baseUrl, issue?.Project, issue?.Key, default),
        };
    }

    /// <summary>Maps a hotspot's state after a status change, read back from <c>hotspots/show</c>.</summary>
    internal static HotspotStatusResult HotspotStatus(HotspotShowResponseDto response, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new HotspotStatusResult
        {
            Key = response.Key,
            Status = response.Status,
            Resolution = response.Resolution,
            Message = response.Message,
            File = response.Component?.Path ?? ComponentKeys.PathFor(response.Component?.Key, ComponentKeys.NoComponents),
            Line = response.Line ?? response.TextRange?.StartLine,
            Url = ComponentKeys.HotspotUrl(baseUrl, response.Project?.Key, response.Key, default),
        };
    }

    // -----------------------------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Whether another page exists. Both halves matter: an empty page is the last one whatever the
    /// total says, and a total SonarQube did not report is not one this server may invent.
    /// </summary>
    private static bool HasMore<T>(PagedResult<T> page) =>
        page.Items.Count > 0 && page.Total is { } total && (long) page.Page * page.PageSize < total;

    /// <summary>Warns when the 10,000-result window is in reach, before the caller pages into it.</summary>
    private static string? CapNote(int? total) =>
        total >= SonarApiClient.MaxResultWindow
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"totalCount is {total}, and SonarQube Cloud only lets the first {SonarApiClient.MaxResultWindow} " +
                $"results be paged through. Narrow the filter to reach the rest.")
            : null;

    private static List<Impact> Impacts(IReadOnlyList<ImpactDto>? impacts)
    {
        if (impacts is null || impacts.Count == 0)
        {
            return [];
        }

        var mapped = new List<Impact>(impacts.Count);

        foreach (var impact in impacts)
        {
            mapped.Add(new Impact
            {
                SoftwareQuality = impact.SoftwareQuality,
                Severity = impact.Severity,
            });
        }

        return mapped;
    }

    private static List<IssueComment> Comments(IReadOnlyList<IssueCommentDto>? comments)
    {
        if (comments is null || comments.Count == 0)
        {
            return [];
        }

        var mapped = new List<IssueComment>(comments.Count);

        foreach (var comment in comments)
        {
            mapped.Add(new IssueComment
            {
                Key = comment.Key,
                Author = comment.Login,
                Text = comment.Markdown ?? comment.HtmlText,
                CreatedAt = comment.CreatedAt,
            });
        }

        return mapped;
    }

    private static List<IssueFlow> Flows(
        IReadOnlyList<FlowDto>? flows,
        IReadOnlyDictionary<string, string> paths)
    {
        if (flows is null || flows.Count == 0)
        {
            return [];
        }

        var mapped = new List<IssueFlow>(flows.Count);

        foreach (var flow in flows)
        {
            var locations = new List<IssueFlowLocation>(flow.Locations?.Count ?? 0);

            foreach (var location in flow.Locations ?? [])
            {
                locations.Add(new IssueFlowLocation
                {
                    File = ComponentKeys.PathFor(location.Component, paths),
                    Component = location.Component,
                    Line = location.TextRange?.StartLine,
                    Message = location.Msg,
                });
            }

            mapped.Add(new IssueFlow { Locations = locations });
        }

        return mapped;
    }

    private static IssueTextRange? TextRange(TextRangeDto? range) =>
        range is null
            ? null
            : new IssueTextRange
            {
                StartLine = range.StartLine,
                EndLine = range.EndLine,
                StartOffset = range.StartOffset,
                EndOffset = range.EndOffset,
            };

    /// <summary>The issue's last line, when it covers more than one — otherwise nothing to say.</summary>
    private static int? EndLine(IssueDto issue)
    {
        var start = issue.Line ?? issue.TextRange?.StartLine;
        var end = issue.TextRange?.EndLine;

        return end is not null && end != start ? end : null;
    }

    /// <summary>Builds the rule-key-to-name map the issue mappers join on.</summary>
    internal static IReadOnlyDictionary<string, string> RuleNames(IReadOnlyList<RuleRefDto>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return NoRuleNames;
        }

        var names = new Dictionary<string, string>(rules.Count, StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            if (!string.IsNullOrEmpty(rule.Key) && !string.IsNullOrEmpty(rule.Name))
            {
                names[rule.Key] = rule.Name;
            }
        }

        return names;
    }

    private static string? RuleName(string? ruleKey, IReadOnlyDictionary<string, string> names) =>
        ruleKey is not null && names.TryGetValue(ruleKey, out var name) ? name : null;

    /// <summary>A rule's page. The rule key travels whole, colon included, as one query value.</summary>
    private static string RuleUrl(string baseUrl, string? ruleKey) =>
        baseUrl + "/coding_rules?open=" + Uri.EscapeDataString(ruleKey ?? string.Empty) +
        "&rule_key=" + Uri.EscapeDataString(ruleKey ?? string.Empty);

    /// <summary>Converts a hotspot's HTML prose, dropping it entirely when there was none.</summary>
    private static string? Prose(string? html)
    {
        var (text, _) = HtmlToText.Convert(html, ToolDefaults.MaxRuleSectionChars);
        return text.Length == 0 ? null : text;
    }

    private static IssueCommentDto? NewestComment(IReadOnlyList<IssueCommentDto>? comments)
    {
        if (comments is null || comments.Count == 0)
        {
            return null;
        }

        IssueCommentDto? newest = null;

        foreach (var comment in comments)
        {
            // By timestamp rather than by position: the response's order is not contractual, and the
            // comment just posted is the one with the latest createdAt. A comment with no timestamp
            // still wins over nothing at all.
            if (newest is null
                || (comment.CreatedAt is { } created && (newest.CreatedAt is null || created > newest.CreatedAt)))
            {
                newest = comment;
            }
        }

        return newest;
    }

    private static bool IsBulkMetric(string? type) =>
        string.Equals(type, "DATA", StringComparison.Ordinal) ||
        string.Equals(type, "DISTRIB", StringComparison.Ordinal);

    private static bool MatchesQuery(MetricDto metric, string query) =>
        Contains(metric.Key, query) || Contains(metric.Name, query) || Contains(metric.Description, query);

    private static bool Contains(string? value, string query) =>
        value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Says which lines the result contains, and warns when the file has no coverage data at all —
    /// an empty <c>uncoveredLines</c> means "fully covered" only if coverage was measured.
    /// </summary>
    private static string CoverageNote(bool measured, bool onlyUncovered, int from, int to)
    {
        var range = string.Create(CultureInfo.InvariantCulture, $"Lines {from}–{to} were read. ");

        if (!measured)
        {
            return range +
                "SonarQube has no coverage data for this file, so uncoveredLines being empty does not mean the " +
                "file is covered — it means coverage was never measured. Check that the analysis publishes a " +
                "coverage report, or read the project's coverage metric with getComponentMeasures.";
        }

        return onlyUncovered
            ? range + "lines lists only the uncovered and partially covered ones; pass onlyUncovered=false for " +
              "every line in the range."
            : range + "lines lists every line in the range.";
    }
}
