using Notifications.Application.RecipientNotices;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 8 (AI-08 NTF-005; AI-24 <c>recipient_notices.templates</c>): the texts
/// the Worker accepts for recipient notices are exactly the ones AI-24 records - each WhatsApp body, each e-mail subject
/// and body, the buttons, the footer, the language, the category and the sender - read from the YAML itself.
/// </summary>
public sealed class RecipientNoticeTextsContractTests
{
    private static readonly Lazy<YamlMappingNode> Templates = new(() =>
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(RepositoryPaths.Normative("specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        stream.Load(reader);
        return Map(Map((YamlMappingNode)stream.Documents[0].RootNode, "recipient_notices"), "templates");
    });

    [Fact]
    public void Whatsapp_bodies_buttons_footer_language_and_category_are_the_ai24_ones()
    {
        var whatsApp = Map(Templates.Value, "whatsapp");
        var bodies = Map(whatsApp, "bodies");
        Assert.Equal(4, bodies.Children.Count);
        foreach (var kind in RecipientNoticeCatalog.All)
        {
            Assert.Equal(Scalar(bodies, RecipientNoticeCatalog.TemplateKey(kind)), RecipientNoticeTexts.WhatsAppBody(kind));
        }

        Assert.Equal(RecipientNoticeTexts.WhatsAppCategory, Scalar(whatsApp, "category"));
        Assert.Equal(RecipientNoticeTexts.WhatsAppLanguage, Scalar(whatsApp, "language"));
        Assert.Equal(RecipientNoticeTexts.WhatsAppQuickReplyButton, Scalar(whatsApp, "quick_reply_button"));
        Assert.Equal(RecipientNoticeTexts.WhatsAppFooter, Scalar(whatsApp, "footer"));
        Assert.StartsWith($"{RecipientNoticeTexts.WhatsAppUrlButton}, URL ", Scalar(whatsApp, "url_button"), StringComparison.Ordinal);
        Assert.StartsWith("{{1}} is the número de guía", Scalar(whatsApp, "body_parameter"), StringComparison.Ordinal);
        Assert.Equal(1, RecipientNoticeTexts.WhatsAppParameterCount);
    }

    [Fact]
    public void Email_subjects_bodies_lines_and_sender_are_the_ai24_ones()
    {
        var email = Map(Templates.Value, "email");
        var subjects = Map(email, "subjects");
        var bodies = Map(email, "bodies");
        var body = Scalar(email, "body");
        Assert.Equal(4, subjects.Children.Count);
        foreach (var kind in RecipientNoticeCatalog.All)
        {
            var key = RecipientNoticeCatalog.TemplateKey(kind);
            Assert.Equal(Scalar(subjects, key), RecipientNoticeTexts.EmailSubject(kind));
            Assert.Equal(Scalar(bodies, key), RecipientNoticeTexts.EmailBody(kind));

            // AI-24 E4-P14: the e-mail body is the WhatsApp body with the button replaced by the link below.
            Assert.Equal(
                RecipientNoticeTexts.WhatsAppBody(kind)
                    .Replace("Con el botón Ver seguimiento", "En el enlace de abajo", StringComparison.Ordinal)
                    .Replace("con el botón Ver seguimiento", "en el enlace de abajo", StringComparison.Ordinal),
                RecipientNoticeTexts.EmailBody(kind));

            // The plain text: the body, the link ({{2}}), the unsubscribe line ({{3}}) and the automatic line.
            Assert.Equal(
                $"{RecipientNoticeTexts.EmailBody(kind)}\n\n{{{{2}}}}\n\n{RecipientNoticeTexts.EmailUnsubscribeLine}\n\n{RecipientNoticeTexts.EmailAutomaticLine}",
                RecipientNoticeTexts.EmailPlainText(kind));
        }

        Assert.Contains($"the line \"{RecipientNoticeTexts.EmailUnsubscribeLine}\"", body, StringComparison.Ordinal);
        Assert.Contains($"the line \"{RecipientNoticeTexts.EmailAutomaticLine}\"", body, StringComparison.Ordinal);
        Assert.Contains("the full tracking link ({{2}}) on a line of its own", body, StringComparison.Ordinal);
        Assert.StartsWith(
            $"{RecipientNoticeTexts.EmailSender}, the ACS MailFrom address of the verified domain paquetenvia.com, with the display\nname {RecipientNoticeTexts.EmailSenderDisplayName};"
                .Replace("\n", " ", StringComparison.Ordinal),
            Scalar(email, "sender"),
            StringComparison.Ordinal);
        Assert.Equal("plain text only, engagement tracking off", Scalar(email, "format"));
        Assert.Equal(3, RecipientNoticeTexts.EmailParameterCount);
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key) =>
        Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;
}
