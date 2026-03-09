
using DotNetEnv;
using GroceryBot;

Env.Load();

await GroceryBotClient.Play();