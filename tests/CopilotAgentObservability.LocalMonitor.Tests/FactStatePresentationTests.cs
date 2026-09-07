using CopilotAgentObservability.LocalMonitor.Presentation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Reflection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class FactStatePresentationTests
{
    public static TheoryData<
        string,
        string,
        string?,
        bool> FixtureMatrix => new()
    {
        {
            "positive",
            "1件",
            null,
            true
        },
        {
            "coverage-proven-zero",
            "0件",
            "取得元: GitHub Copilot CLI 1.0.75。このセッションの対象記録を完全に確認しました。",
            true
        },
        {
            "not-observed",
            "なし",
            null,
            false
        },
        {
            "unsupported",
            "未対応",
            "取得元: GitHub Copilot Chat。この取得元はスキルを識別する情報を提供しません。",
            false
        },
        {
            "capture-gap",
            "一部欠落",
            "取得元: GitHub Copilot Chat。表示用データ更新段階でトークン値を保持できませんでした。",
            false
        },
        {
            "malformed",
            "読取不可",
            "形式を確認できない記録が含まれています。",
            false
        },
        {
            "oversized",
            "読取不可",
            "表示上限を超えた記録が含まれています。",
            false
        },
        {
            "projection-invalid",
            "読取不可",
            "表示用データ更新段階で整合性を確認できませんでした。",
            false
        },
        {
            "certification-pending",
            "3件",
            "未確認。取得元: GitHub Copilot CLI。取得条件を限定した確認がまだ完了していません。",
            true
        },
        {
            "expired",
            "期限切れ",
            "この内容の保存期間は終了しています。",
            false
        },
        {
            "redacted",
            "なし",
            "機密情報を除外したため内容を保存していません。",
            false
        },
        {
            "not-captured",
            "なし",
            "取得時に内容の保存が有効ではありませんでした。",
            false
        },
        {
            "inconsistent-cache",
            "不整合",
            "値が一致しません。キャッシュ値が入力トークン合計を上回っています。",
            false
        },
        {
            "mixed-source-version",
            "なし",
            "取得元: 複数の取得元・バージョン。完全な対象範囲を証明できません。",
            false
        },
        {
            "archived-context",
            "2件",
            "アーカイブされたセッションの記録です。",
            true
        },
    };

    public static TheoryData<int, string?, string?> UnavailableExplanationCases => new()
    {
        { (int)FactState.Unsupported, null, "理由" },
        { (int)FactState.Unsupported, "取得元", null },
        { (int)FactState.CaptureGap, "取得元", null },
        { (int)FactState.RawNotCaptured, null, null },
        { (int)FactState.RawExpired, null, null },
    };

    public static TheoryData<string> InternalWireIdentifiers => new()
    {
        "observed_positive",
        "observed-positive",
        "observed_zero",
        "observed-zero",
        "not_observed",
        "not-observed",
        "capture_gap",
        "capture-gap",
        "certification_pending",
        "certification-pending",
        "raw_not_captured",
        "raw-not-captured",
        "not_captured",
        "not-captured",
        "raw_expired",
        "raw-expired",
        "projection_invalid",
        "projection-invalid",
        "expired_pending_deletion",
        "expired-pending-deletion",
    };

    public static TheoryData<string> InternalWholeFieldTokens => new()
    {
        "ObservedPositive",
        "ObservedZero",
        "NotObserved",
        "Unsupported",
        "CaptureGap",
        "CertificationPending",
        "RawNotCaptured",
        "NotCaptured",
        "RawExpired",
        "Inconsistent",
        "ProjectionInvalid",
        "ExpiredPendingDeletion",
        "Malformed",
        "Oversized",
        "Redacted",
        "unsupported",
        "inconsistent",
        "malformed",
        "oversized",
        "redacted",
    };

    [Theory]
    [MemberData(nameof(FixtureMatrix))]
    public void Resolve_MapsCompleteFixtureMatrixWithoutInventingFacts(
        string fixture,
        string expectedPrimary,
        string? expectedDetail,
        bool expectedDerivedVisualization)
    {
        var presentation = FactStatePresentation.Resolve(RequestForFixture(fixture));

        Assert.Equal(expectedPrimary, presentation.PrimaryText);
        Assert.Equal(expectedDetail, presentation.DetailText);
        Assert.Equal(expectedDerivedVisualization, presentation.AllowsDerivedVisualization);
    }

    [Fact]
    public void Resolve_ZeroCountWithoutCompleteCoverageUsesOpenWorldAbsenceWording()
    {
        var presentation = FactStatePresentation.Resolve(
            new(FactState.ObservedZero, new RecordedFactCount(0)));

        Assert.Equal("なし", presentation.PrimaryText);
        Assert.DoesNotContain("0件", presentation.PrimaryText);
        Assert.False(presentation.AllowsDerivedVisualization);
    }

    [Fact]
    public void Resolve_ZeroCountWithCompleteCoverageUsesDeterministicZeroLabel()
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.ObservedZero,
                new RecordedFactCount(0),
                HasCompleteCoverageProof: true,
                new(
                    "GitHub Copilot CLI 1.0.75",
                    "このセッションの対象記録を完全に確認しました。")));

        Assert.Equal("0件", presentation.PrimaryText);
        Assert.True(presentation.AllowsDerivedVisualization);
    }

    [Theory]
    [InlineData(null, "このセッションの対象記録を完全に確認しました。")]
    [InlineData("GitHub Copilot CLI 1.0.75", null)]
    public void Resolve_CompleteZeroRequiresSourceAndCoverageExplanation(
        string? source,
        string? reason)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new(
                    FactState.ObservedZero,
                    new RecordedFactCount(0),
                    HasCompleteCoverageProof: true,
                    new(source, reason))));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public void Resolve_ExplicitZeroStateRejectsAPositiveCount()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new(
                    FactState.ObservedZero,
                    new RecordedFactCount(1),
                    HasCompleteCoverageProof: true,
                    new(
                        "GitHub Copilot CLI 1.0.75",
                        "このセッションの対象記録を完全に確認しました。"))));

        Assert.Equal("request", exception.ParamName);
    }

    [Theory]
    [InlineData((int)FactState.ObservedPositive)]
    [InlineData((int)FactState.CertificationPending)]
    public void Resolve_ZeroCountRejectsPositiveOrCertificationPendingState(
        int stateValue)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new(
                    (FactState)stateValue,
                    new RecordedFactCount(0))));

        Assert.Equal("request", exception.ParamName);
    }

    [Theory]
    [InlineData((int)FactState.ObservedPositive)]
    [InlineData((int)FactState.ObservedZero)]
    [InlineData((int)FactState.CertificationPending)]
    public void Resolve_ObservedStateRequiresARecordedCount(int stateValue)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(new((FactState)stateValue)));

        Assert.Equal("request", exception.ParamName);
    }

    [Theory]
    [InlineData(1UL, "1件")]
    [InlineData(12UL, "12件")]
    [InlineData(1234UL, "1234件")]
    public void Resolve_PositiveCountUsesDeterministicDisplayLabel(
        ulong count,
        string expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        try
        {
            var presentation = FactStatePresentation.Resolve(
                new(
                    FactState.ObservedPositive,
                    new RecordedFactCount(count)));

            Assert.Equal(expected, presentation.PrimaryText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Resolve_CertificationPendingPreservesDerivedPositiveCountLabel()
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.CertificationPending,
                new RecordedFactCount(12)));

        Assert.Equal("12件", presentation.PrimaryText);
        Assert.Contains("未確認", presentation.DetailText);
    }

    [Fact]
    public void Resolve_InconsistentStateAcceptsNoRecordedCount()
    {
        var presentation = FactStatePresentation.Resolve(
            new(FactState.Inconsistent));

        Assert.Equal("不整合", presentation.PrimaryText);
        Assert.False(presentation.AllowsDerivedVisualization);
    }

    [Theory]
    [InlineData((int)FactState.RawDeleted)]
    [InlineData((int)FactState.RawReadDenied)]
    public void Resolve_DeletedAndReadDeniedKeepDistinctConciseLabels(int stateValue)
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                (FactState)stateValue,
                Explanation: new(ReasonText: "保存済み内容へアクセスできません。")));

        Assert.Equal(stateValue == (int)FactState.RawDeleted ? "削除済み" : "表示不可", presentation.PrimaryText);
        Assert.False(presentation.AllowsDerivedVisualization);
    }

    [Fact]
    public void Resolve_ProjectionInvalidKeepsItsDistinctConciseLabel()
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.ProjectionInvalid,
                Explanation: new(ReasonText: "表示用データ更新結果を安全に表示できません。")));

        Assert.Equal("読取不可", presentation.PrimaryText);
        Assert.False(presentation.AllowsDerivedVisualization);
    }

    [Theory]
    [MemberData(nameof(UnavailableExplanationCases))]
    public void Resolve_RequiresTheAuthorizedExplanationForUnavailableStates(
        int stateValue,
        string? source,
        string? reason)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new((FactState)stateValue, Explanation: new(source, reason))));

        Assert.Equal("request", exception.ParamName);
    }

    [Theory]
    [InlineData((int)FactState.NotObserved)]
    [InlineData((int)FactState.Unsupported)]
    [InlineData((int)FactState.CaptureGap)]
    [InlineData((int)FactState.RawNotCaptured)]
    [InlineData((int)FactState.RawExpired)]
    [InlineData((int)FactState.Inconsistent)]
    public void Resolve_StateWithoutQuantitativeValueRejectsARecordedCount(int stateValue)
    {
        var state = (FactState)stateValue;
        var explanation = state == FactState.Unsupported
            ? new FactStateExplanation("GitHub Copilot CLI", "取得能力を確認しました。")
            : new FactStateExplanation(ReasonText: "取得状況を確認しました。");

        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new(
                    state,
                    new RecordedFactCount(1),
                    Explanation: explanation)));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public void Explanation_RejectsSourceOrReasonBeyondTheComponentBound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FactStateExplanation(new string('取', 81), "理由"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FactStateExplanation("取得元", new string('理', 241)));
    }

    [Fact]
    public void Resolve_DoesNotGenerateInternalStateOrReasonTokens()
    {
        var presentations = new[]
        {
            FactStatePresentation.Resolve(new(FactState.NotObserved)),
            FactStatePresentation.Resolve(
                new(
                    FactState.Unsupported,
                    Explanation: new("取得元", "必要な情報が提供されません。"))),
            FactStatePresentation.Resolve(
                new(
                    FactState.CaptureGap,
                    Explanation: new(ReasonText: "表示用データ更新段階で記録を保持できませんでした。"))),
            FactStatePresentation.Resolve(
                new(
                    FactState.CertificationPending,
                    new RecordedFactCount(1))),
            FactStatePresentation.Resolve(
                new(
                    FactState.RawExpired,
                    Explanation: new(ReasonText: "保存期間が終了しています。"))),
            FactStatePresentation.Resolve(new(FactState.Inconsistent)),
        };
        var forbiddenTokens = new[]
        {
            "not_observed",
            "unsupported",
            "capture_gap",
            "certification_pending",
            "raw_expired",
            "inconsistent",
            nameof(FactState.NotObserved),
            nameof(FactState.Unsupported),
            nameof(FactState.CaptureGap),
            nameof(FactState.CertificationPending),
            nameof(FactState.RawExpired),
            nameof(FactState.Inconsistent),
        };

        foreach (var presentation in presentations)
        {
            var renderedText = $"{presentation.PrimaryText} {presentation.DetailText}";
            foreach (var forbidden in forbiddenTokens)
            {
                Assert.DoesNotContain(forbidden, renderedText, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [MemberData(nameof(InternalWireIdentifiers))]
    public void Explanation_RejectsEmbeddedInternalWireIdentifier(
        string internalIdentifier)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FactStateExplanation(
                "GitHub Copilot CLI",
                $"診断コード={internalIdentifier}; 取得状況を確認しました。"));

        Assert.Equal("ReasonText", exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(InternalWholeFieldTokens))]
    public void Explanation_RejectsWholeFieldInternalContractToken(
        string internalToken)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FactStateExplanation(
                internalToken,
                "取得状況を確認しました。"));

        Assert.Equal("SourceText", exception.ParamName);
    }

    [Theory]
    [InlineData("RawExpired")]
    [InlineData("ExpiredPendingDeletion")]
    [InlineData("CaptureGap")]
    public void Explanation_RejectsEmbeddedEnumStyleIdentifierInReasonText(
        string internalIdentifier)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FactStateExplanation(
                "CaptureGap SDK 1.0",
                $"診断コード={internalIdentifier}; 取得状況を確認しました。"));

        Assert.Equal("ReasonText", exception.ParamName);
    }

    [Theory]
    [InlineData("source", "capture_gap")]
    [InlineData("source", "projection_invalid")]
    [InlineData("reason", "capture_gap")]
    [InlineData("reason", "projection_invalid")]
    public void Resolve_RejectsReservedContractTokenEmbeddedInRequiredExplanation(
        string field,
        string reservedToken)
    {
        var source = field == "source"
            ? $"GitHub Copilot CLI ({reservedToken})"
            : "GitHub Copilot CLI";
        var reason = field == "reason"
            ? $"取得能力を確認しました ({reservedToken})"
            : "取得能力を確認しました。";

        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new(
                    FactState.Unsupported,
                    Explanation: new(source, reason))));

        Assert.Equal(
            field == "source" ? "SourceText" : "ReasonText",
            exception.ParamName);
    }

    [Theory]
    [InlineData("Redacted SDK 1.0")]
    [InlineData("Malformed-compatible source")]
    [InlineData("Unsupported mode reporter")]
    [InlineData("CaptureGap SDK 1.0")]
    [InlineData("RawExpired SDK 1.0")]
    [InlineData("ExpiredPendingDeletion SDK 1.0")]
    public void Resolve_PreservesSafeNaturalSourceProse(string source)
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.NotObserved,
                Explanation: new(source, "取得状況を確認しました。")));

        Assert.Contains($"取得元: {source}。", presentation.DetailText);
    }

    [Theory]
    [InlineData("capture_gap")]
    [InlineData("projection_invalid")]
    [InlineData("expired_pending_deletion")]
    public void Resolve_InvalidOptionalExplanationFailsInsteadOfFallingBack(
        string internalIdentifier)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            FactStatePresentation.Resolve(
                new(
                    FactState.NotObserved,
                    Explanation: new(
                        "GitHub Copilot CLI 1.0.75",
                        $"診断コード={internalIdentifier}; 取得状況を確認しました。"))));

        Assert.Equal("ReasonText", exception.ParamName);
    }

    [Fact]
    public void ComponentInputHasNoSanitizedOnlyBranch()
    {
        var propertyNames = typeof(FactStatePresentationRequest)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Sanitized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolvedPresentationExposesOnlySafeRenderedFields()
    {
        var propertyNames = typeof(FactStatePresentation)
            .GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                nameof(FactStatePresentation.AllowsDerivedVisualization),
                nameof(FactStatePresentation.DetailText),
                nameof(FactStatePresentation.PrimaryText),
            ],
            propertyNames);
    }

    [Fact]
    public void QuantitativeRequestSurfaceHasNoArbitraryDisplayStringPath()
    {
        var requestType = typeof(FactStatePresentationRequest);
        Assert.Null(requestType.GetProperty("RecordedValue"));

        var countProperty = requestType.GetProperty("RecordedCount");
        Assert.NotNull(countProperty);
        var countType = Nullable.GetUnderlyingType(countProperty.PropertyType);
        Assert.NotNull(countType);
        Assert.DoesNotContain(
            countType.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(string));
        Assert.DoesNotContain(
            countType.GetProperties(),
            property => property.PropertyType == typeof(string));
    }

    [Fact]
    public void ResolvedPresentationHasNoCallableConstructorBypass()
    {
        var callableConstructors = typeof(FactStatePresentation)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor => !constructor.IsPrivate);

        Assert.Empty(callableConstructors);
    }

    [Fact]
    public async Task Partial_RendersValueAndExplanationAsEscapedText()
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.ObservedPositive,
                new RecordedFactCount(1),
                Explanation: new(
                    "<取得元>",
                    "<img src=x onerror=alert(1)> を文字として表示します。")));

        var html = await RenderPartialAsync(presentation);
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("1件", decoded);
        Assert.Contains("取得元: <取得元>。", decoded);
        Assert.Contains("<img src=x onerror=alert(1)>", decoded);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_UsesKeyboardReachableExpandableExplanationAndVisibleText()
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.NotObserved,
                Explanation: new(
                    "GitHub Copilot Chat",
                    "このセッションの記録だけを確認しました。")));

        var html = await RenderPartialAsync(presentation);
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains(
            "<span class=\"fact-state-primary\">なし</span>",
            decoded);
        Assert.Contains("<details class=\"fact-state-explanation\">", decoded);
        Assert.Contains("<summary>表示の理由</summary>", decoded);
        Assert.Contains("このセッションの記録だけを確認しました。", decoded);
        Assert.DoesNotContain("実際に使われなかった", decoded);
        Assert.DoesNotContain("title=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_InconsistentValueDoesNotRenderRatioBarPercentageOrInternalToken()
    {
        var presentation = FactStatePresentation.Resolve(
            new(
                FactState.Inconsistent,
                Explanation: new(ReasonText: "キャッシュ値が入力トークン合計を上回っています。")));

        var html = await RenderPartialAsync(presentation);
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains("不整合", decoded);
        Assert.Contains("値が一致しません", decoded);
        Assert.DoesNotContain("125%", decoded);
        Assert.DoesNotContain("<meter", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<progress", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role=\"progressbar\"", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(FactState.Inconsistent), decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-state", decoded, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RenderPartialAsync(FactStatePresentation presentation)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MonitorOptions).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddRazorPages();
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        var viewEngine = services.GetRequiredService<IRazorViewEngine>();
        var viewResult = viewEngine.GetView(
            executingFilePath: null,
            viewPath: "/Pages/Shared/_FactStatePresentation.cshtml",
            isMainPage: false);
        Assert.True(
            viewResult.Success,
            $"Partial view was not found. Searched: {string.Join(", ", viewResult.SearchedLocations)}");

        var viewData = new ViewDataDictionary<FactStatePresentation>(
            services.GetRequiredService<IModelMetadataProvider>(),
            new ModelStateDictionary())
        {
            Model = presentation,
        };
        var tempData = new TempDataDictionary(
            httpContext,
            services.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewData,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private static FactStatePresentationRequest RequestForFixture(string fixture) =>
        fixture switch
        {
            "positive" => new(
                FactState.ObservedPositive,
                new RecordedFactCount(1)),
            "coverage-proven-zero" => new(
                FactState.ObservedZero,
                new RecordedFactCount(0),
                HasCompleteCoverageProof: true,
                new(
                    "GitHub Copilot CLI 1.0.75",
                    "このセッションの対象記録を完全に確認しました。")),
            "not-observed" => new(FactState.NotObserved),
            "unsupported" => new(
                FactState.Unsupported,
                Explanation: new(
                    "GitHub Copilot Chat",
                    "この取得元はスキルを識別する情報を提供しません。")),
            "capture-gap" => new(
                FactState.CaptureGap,
                Explanation: new(
                    "GitHub Copilot Chat",
                    "表示用データ更新段階でトークン値を保持できませんでした。")),
            "malformed" => new(
                FactState.ProjectionInvalid,
                Explanation: new(ReasonText: "形式を確認できない記録が含まれています。")),
            "oversized" => new(
                FactState.ProjectionInvalid,
                Explanation: new(ReasonText: "表示上限を超えた記録が含まれています。")),
            "projection-invalid" => new(
                FactState.ProjectionInvalid,
                Explanation: new(ReasonText: "表示用データ更新段階で整合性を確認できませんでした。")),
            "certification-pending" => new(
                FactState.CertificationPending,
                new RecordedFactCount(3),
                Explanation: new(
                    "GitHub Copilot CLI",
                    "取得条件を限定した確認がまだ完了していません。")),
            "expired" => new(
                FactState.RawExpired,
                Explanation: new(ReasonText: "この内容の保存期間は終了しています。")),
            "redacted" => new(
                FactState.RawNotCaptured,
                Explanation: new(ReasonText: "機密情報を除外したため内容を保存していません。")),
            "not-captured" => new(
                FactState.RawNotCaptured,
                Explanation: new(ReasonText: "取得時に内容の保存が有効ではありませんでした。")),
            "inconsistent-cache" => new(
                FactState.Inconsistent,
                Explanation: new(ReasonText: "キャッシュ値が入力トークン合計を上回っています。")),
            "mixed-source-version" => new(
                FactState.ObservedZero,
                new RecordedFactCount(0),
                Explanation: new(
                    "複数の取得元・バージョン",
                    "完全な対象範囲を証明できません。")),
            "archived-context" => new(
                FactState.ObservedPositive,
                new RecordedFactCount(2),
                Explanation: new(ReasonText: "アーカイブされたセッションの記録です。")),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };
}
