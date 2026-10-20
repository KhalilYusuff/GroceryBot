# GroceryBot

GroceryBot is a C# bot for the [AINM Grocery Bot challenge](https://app.ainm.no/challenge).  
It connects to the game server over WebSocket, reads game state each round, and sends actions for each bot.

## Requirements

- .NET 10 SDK

## Setup

1. Create a `.env` file in the repository root:

```env
GROCERY_WS_URL=wss://game.ainm.no/ws?token=YOUR_TOKEN
# Optional:
# GROCERY_DEBUG=1
# GROCERY_TIMING=1
```

2. Get a fresh game token from the challenge page and place it in `GROCERY_WS_URL`.

## Run

```bash
dotnet run --project /home/runner/work/GroceryBot/GroceryBot/GroceryBot/GroceryBot.csproj
```

## Build

```bash
dotnet build /home/runner/work/GroceryBot/GroceryBot/GroceryBot.sln
```

## Additional docs

- `/home/runner/work/GroceryBot/GroceryBot/GroceryBot/GroceryBotDocs.md` contains protocol and gameplay details.
