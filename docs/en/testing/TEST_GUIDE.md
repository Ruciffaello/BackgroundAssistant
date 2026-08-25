# BackgroundAssistant Testing Guide

This document lists test inputs and expected behaviors for evaluating **BackgroundAssistant** across different operational stages.

## 1. System Warm-up & Initialization

Once the host finishes loading models:
- **Expected Behavior**: Console logs `System warm-up complete`, followed by voice TTS output: *"暖身完畢，我已經準備好為您服務了。"*

## 2. Basic Tool Execution Tests

| Feature | Sample Voice/CMD Input | Expected Tool | Expected Voice Output |
| :--- | :--- | :--- | :--- |
| **Local Time** | "現在幾點了？" (What time is it?) | `TimeTools` | Reports local time. |
| **RSS News** | "台灣總統新聞" (Taiwan president news) | `RssNewsTools` | Reads top 3 news items with 200ms pauses between items. |
| **Pokemon Cards** | "幫我找噴火龍的卡牌" (Find Charizard cards) | `PtcgTools` | Reports Charizard card info. |
| **File Search** | "幫我找履歷.pdf" (Find resume.pdf) | `FileSearchTool` (DLL) | Displays matching file paths in console (no voice spam). |
| **System Exit** | "關閉系統" (Shutdown system) | `SystemTools` | Announces shutdown and exits cleanly in 2.5s. |

General knowledge, chat, and chit-chat route to `conversation` and are handled directly by the conversation model.

## 3. Dual Input: Microphone (STT) & Terminal (CMD)

- **CMD Input**: Type queries directly into the console window (e.g., `現在幾點`, `幫我找皮卡丘`) and press Enter. CMD input bypasses the spoken speech refiner and routes directly to the LLM Router.
- **Quick Exit**: Type `exit`, `quit`, `q`, or `結束` to trigger a graceful shutdown.

## 4. Spoken Speech Refiner (Pfiller Filtering)

- Input: *"那個... 呃... 幫我查一下現在幾點。"*
- Cleaned Text: *"現在幾點。"* -> Triggers time tool.

## 5. Console Output Stages

When running the application, each turn prints structured stage tags:

1. `[1. STT Result]` / `[1. CMD Input]`: Raw input source and text.
2. `[2. Refined Text]`: Cleaned text after spoken filler removal.
3. `[2. BM25 Context]`: BM25 score and inclusion status for historical turns.
4. `[3. Decision Router]`: Raw routing JSON (`conversation` vs `tool`).
5. `[3. Chat]` / `[3. Tool Command]`: Conversational answer or direct tool invocation.
6. `[4. Execution Result]`: Text result produced by tool execution.
7. `[5. TTS Speaking]`: Final text passed to SherpaOnnx speech synthesis.
