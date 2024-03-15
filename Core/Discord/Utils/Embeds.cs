﻿using DSharpPlus;
using DSharpPlus.Entities;

namespace Vint.Core.Discord.Utils;

static class Embeds {
    public static async Task SendEmbed(this DiscordInteraction interaction, DiscordEmbed embed, bool ephemeral = true) =>
        await interaction.CreateResponseAsync(
            InteractionResponseType.ChannelMessageWithSource,
            new DiscordInteractionResponseBuilder()
                .AddEmbed(embed)
                .AsEphemeral(ephemeral)
        );

    public static DiscordEmbedBuilder AddFields(this DiscordEmbedBuilder builder, params (string name, string value, bool inline)[] fields) {
        foreach ((string name, string value, bool inline) in fields)
            builder.AddField(name, value, inline);

        return builder;
    }

    public static DiscordEmbedBuilder GetClosedDMsEmbed(string description) => GetErrorEmbed(
        $"{description}\n\nНажмите ПКМ по серверу, выберите \"Настройки конфиденциальности\" и включите пункт \"Личные сообщения\".");

    public static DiscordEmbedBuilder GetSuccessfulEmbed(string description, string title = "Success", string? footer = null) =>
        GetEmbed(title, description, footer, "#44944A");

    public static DiscordEmbedBuilder GetNotificationEmbed(string description, string title = "Information", string? footer = null) =>
        GetEmbed(title, description, footer, "#6495ED");

    public static DiscordEmbedBuilder GetWarningEmbed(string description, string title = "Warning", string? footer = null) =>
        GetEmbed(title, description, footer, "#FFCC00");

    public static DiscordEmbedBuilder GetErrorEmbed(string description, string title = "Error", bool critical = true) =>
        GetEmbed(title,
            description,
            critical ? "Просим вас сообщать о неправильном поведении бота на сервер поддержки." : null,
            "#CC0605");

    static DiscordEmbedBuilder GetEmbed(string title, string description, string? footer, string color) => new DiscordEmbedBuilder()
        .WithTitle(title)
        .WithDescription(description)
        .WithColor(new DiscordColor(color))
        .WithFooter(footer!);
}