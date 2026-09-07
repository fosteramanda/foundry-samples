// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace VoiceHostedAgent;

internal sealed record ResponsePlan(
    string Text,
    bool UseModel,
    bool Streaming,
    BinaryData? Voice)
{
    internal static ResponsePlan Model(
        string prompt,
        bool streaming = true,
        BinaryData? voice = null) =>
        new(prompt, UseModel: true, streaming, voice);

    internal static ResponsePlan Fixed(
        string text,
        bool streaming = false,
        BinaryData? voice = null) =>
        new(text, UseModel: false, streaming, voice);
}
