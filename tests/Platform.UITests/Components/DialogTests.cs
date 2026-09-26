using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class DialogTests : ComponentTest
{
    private const string FocusIdentifier = "Blazor._internal.domWrapper.focus";
    private readonly ElementReference _opener;

    public DialogTests() => _opener = new ElementReference("opener-button", new WebElementReferenceContext(JSInterop.JSRuntime));

    [Fact]
    public void Dialog_closes_on_Escape_and_returns_focus()
    {
        var changes = new List<bool>();
        var cut = RenderOpenDialog(p => p.Add(d => d.OpenChanged, v => changes.Add(v)));

        cut.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        changes.ShouldBe([false]);
        cut.FindAll("[role=dialog]").ShouldBeEmpty();
        LastFocusedId().ShouldBe(_opener.Id);
    }

    [Fact]
    public void Other_keys_do_not_close_the_dialog()
    {
        var changes = new List<bool>();
        var cut = RenderOpenDialog(p => p.Add(d => d.OpenChanged, v => changes.Add(v)));

        cut.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        changes.ShouldBeEmpty();
        cut.FindAll("[role=dialog]").Count.ShouldBe(1);
    }

    [Fact]
    public void Dialog_opens_with_the_title_as_its_name_and_focus_on_cancel()
    {
        var cut = RenderOpenDialog();

        var dialog = cut.Find("[role=dialog]");
        dialog.GetAttribute("aria-modal").ShouldBe("true");
        cut.Find($"#{dialog.GetAttribute("aria-labelledby")}").TextContent.ShouldBe("Revoke access for Sara Ahmed?");
        cut.Find($"#{dialog.GetAttribute("aria-describedby")}").TextContent.ShouldContain("loses access at once");
        LastFocusedId().ShouldBe(RefId(ButtonNamed(cut, "Cancel")));
    }

    [Fact]
    public void Dialog_traps_focus_between_its_guards()
    {
        var cut = RenderOpenDialog();
        var guards = cut.FindAll("[data-focus-guard]");
        guards.Select(g => g.GetAttribute("data-focus-guard")).ShouldBe(["start", "end"]);
        guards.ShouldAllBe(g => g.GetAttribute("tabindex") == "0");

        cut.Find("[data-focus-guard=end]").Focus();
        LastFocusedId().ShouldBe(RefId(cut.Find("[role=dialog]")));

        cut.Find("[data-focus-guard=start]").Focus();
        LastFocusedId().ShouldBe(RefId(ButtonNamed(cut, "Revoke access")));
    }

    [Fact]
    public void Confirm_repeats_the_verb_and_raises_confirm_without_closing()
    {
        var confirmed = 0;
        var changes = new List<bool>();
        var cut = RenderOpenDialog(p => p
            .Add(d => d.OnConfirm, () => confirmed++)
            .Add(d => d.OpenChanged, v => changes.Add(v)));

        var confirm = ButtonNamed(cut, "Revoke access");
        confirm.ClassList.ShouldContain("bg-danger");
        confirm.Click();

        confirmed.ShouldBe(1);
        changes.ShouldBeEmpty();
    }

    [Fact]
    public void Cancel_closes_the_dialog()
    {
        var changes = new List<bool>();
        var cut = RenderOpenDialog(p => p.Add(d => d.OpenChanged, v => changes.Add(v)));

        ButtonNamed(cut, "Cancel").Click();

        changes.ShouldBe([false]);
        cut.FindAll("[role=dialog]").ShouldBeEmpty();
        LastFocusedId().ShouldBe(_opener.Id);
    }

    [Fact]
    public void While_busy_the_dialog_stays_open_and_confirm_shows_loading()
    {
        var changes = new List<bool>();
        var cut = RenderOpenDialog(p => p.Add(d => d.Busy, true).Add(d => d.OpenChanged, v => changes.Add(v)));

        cut.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        changes.ShouldBeEmpty();
        ButtonNamed(cut, "Cancel").HasAttribute("disabled").ShouldBeTrue();
        ButtonNamed(cut, "Revoke access").GetAttribute("aria-busy").ShouldBe("true");
    }

    [Fact]
    public void A_closed_dialog_renders_nothing()
    {
        var cut = Render<Dialog>(p => p.Add(d => d.Open, false).Add(d => d.Title, "Revoke access?").Add(d => d.ConfirmText, "Revoke access"));

        cut.Markup.Trim().ShouldBeEmpty();
    }

    private IRenderedComponent<Dialog> RenderOpenDialog(Action<ComponentParameterCollectionBuilder<Dialog>>? extra = null) =>
        Render<Dialog>(p =>
        {
            p.Add(d => d.Open, true)
                .Add(d => d.Title, "Revoke access for Sara Ahmed?")
                .Add(d => d.ConfirmText, "Revoke access")
                .Add(d => d.ConfirmVariant, ButtonVariant.Danger)
                .Add(d => d.ReturnFocus, _opener)
                .AddChildContent("<p>Sara Ahmed loses access at once. Past actions stay in the audit log.</p>");
            extra?.Invoke(p);
        });

    private static IElement ButtonNamed(IRenderedComponent<Dialog> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    private static string RefId(IElement element) =>
        element.GetAttribute("blazor:elementReference") ?? throw new InvalidOperationException("The element has no @ref.");

    private string LastFocusedId() =>
        ((ElementReference)JSInterop.Invocations[FocusIdentifier][^1].Arguments[0]!).Id;
}
