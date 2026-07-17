# Heads-Up for Windows

## Project Overview

Create a Windows desktop application in C# (.NET 8) that monitors a Slack workspace and provides highly visible, Teams-style notifications whenever a Slack Huddle is started.

The goal is to make Slack Huddle invitations significantly more noticeable than the default Slack desktop notifications.

This application is intended for personal use and should run as a lightweight background application from the Windows system tray.

---

# Primary Goal

When another user starts a Slack Huddle, display a prominent Windows notification that:

- Is larger and more visible than a standard Slack notification.
- Shows who started the huddle.
- Shows the Slack channel or DM.
- Allows the user to join immediately.
- Optionally plays a custom sound.
- Can be configured to prioritize specific users.

The experience should be similar to an incoming Microsoft Teams meeting notification.

---

# Technology Stack

Use the following technologies:

- C# 12
- .NET 8
- WPF
- MVVM pattern
- CommunityToolkit.Mvvm
- CommunityToolkit.WinUI.Notifications
- SlackNet (preferred) or equivalent Slack SDK
- Microsoft.Extensions.Hosting
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Logging
- Serilog

---

# Development Priorities

Priority order:

1. Reliable Slack connectivity
2. Accurate huddle detection
3. Stable notifications
4. Polish and user experience
5. Additional features

The final result should feel like a polished native Windows utility, not a proof-of-concept application.
