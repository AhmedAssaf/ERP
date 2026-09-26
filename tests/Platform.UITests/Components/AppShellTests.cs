using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class AppShellTests : ComponentTest
{
    [Fact]
    public void AppShell_tenant_variant_shows_the_tenant_logo_and_name()
    {
        var cut = RenderShell(p => p
            .Add(s => s.Variant, AppShellVariant.Tenant)
            .Add(s => s.PortalName, "Acme Contracting")
            .Add(s => s.LogoUrl, "/branding/acme/logo.png"));

        var logo = cut.Find("header img");
        logo.GetAttribute("src").ShouldBe("/branding/acme/logo.png");
        logo.GetAttribute("alt").ShouldBe(string.Empty);
        cut.Find("header").TextContent.ShouldContain("Acme Contracting");
        cut.Find("header").TextContent.ShouldNotContain("WaslaBid");
        cut.Find("header").ClassList.ShouldContain("bg-primary");
    }

    [Fact]
    public void AppShell_tenant_variant_without_a_logo_shows_the_name_only()
    {
        var cut = RenderShell(p => p.Add(s => s.Variant, AppShellVariant.Tenant).Add(s => s.PortalName, "Acme Contracting"));

        cut.FindAll("header img").ShouldBeEmpty();
        cut.Find("header").TextContent.ShouldContain("Acme Contracting");
    }

    [Fact]
    public void AppShell_platform_variant_shows_the_WaslaBid_mark()
    {
        var cut = RenderShell(p => p.Add(s => s.Variant, AppShellVariant.Platform).Add(s => s.PortalName, "ignored"));

        cut.FindAll("header img").ShouldBeEmpty();
        var header = cut.Find("header");
        header.TextContent.ShouldContain("WaslaBid");
        header.TextContent.ShouldContain("Platform console");
        header.TextContent.ShouldNotContain("ignored");
    }

    [Fact]
    public void AppShell_has_navigation_main_content_and_a_skip_link()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/users");

        var cut = RenderShell();

        var nav = cut.Find("nav");
        nav.GetAttribute("aria-label").ShouldBe("Main navigation");
        nav.InnerHtml.ShouldContain("Users");
        var main = cut.Find("main");
        main.Id.ShouldBe("main");
        main.TextContent.ShouldContain("Page body");
        var skip = cut.FindAll("a")[0];
        skip.TextContent.ShouldBe("Skip to main content");
        skip.GetAttribute("href").ShouldBe("http://localhost/admin/users#main");
    }

    [Fact]
    public void AppShell_culture_switch_returns_to_the_current_page()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/users?page=2");

        var cut = RenderShell();

        var link = cut.Find("a[hreflang]");
        link.TextContent.ShouldBe("العربية");
        link.GetAttribute("hreflang").ShouldBe("ar");
        link.GetAttribute("lang").ShouldBe("ar");
        link.GetAttribute("href").ShouldBe("/culture/set?culture=ar-SA&returnUrl=%2Fadmin%2Fusers%3Fpage%3D2");
    }

    [Fact]
    public void AppShell_in_arabic_offers_english()
    {
        UseCulture("ar-SA");

        var cut = RenderShell();

        var link = cut.Find("a[hreflang]");
        link.TextContent.ShouldBe("English");
        link.GetAttribute("href")!.ShouldStartWith("/culture/set?culture=en-US&returnUrl=");
        cut.Find("summary").TextContent.ShouldContain("Sara Ahmed");
    }

    [Fact]
    public void AppShell_user_menu_signs_out_with_a_post_form()
    {
        var cut = RenderShell();

        cut.Find("summary").TextContent.ShouldContain("Sara Ahmed");
        var form = cut.Find("details form");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe("/account/sign-out");
        form.QuerySelector("button[type=submit]")!.TextContent.Trim().ShouldBe("Sign out");
    }

    private IRenderedComponent<AppShell> RenderShell(Action<ComponentParameterCollectionBuilder<AppShell>>? extra = null) =>
        Render<AppShell>(p =>
        {
            p.Add(s => s.UserName, "Sara Ahmed")
                .Add(s => s.SignOutUrl, "/account/sign-out")
                .Add(s => s.Navigation, "<a href=\"/admin/users\">Users</a>")
                .AddChildContent("<p>Page body</p>");
            extra?.Invoke(p);
        });
}
