Grocery Bot — Game AI
Build a bot that controls a swarm of workers in a procedurally generated grocery store. Your bots navigate the store, pick items from shelves, and deliver them to the drop-off zone to fulfill orders sequentially. Bot count scales by difficulty — from 1 bot to 20.
How It Works
Your bot connects to the game server via WebSocket. Each round, the server sends you the full game state, and you respond with actions for each bot.
Server → Your Bot: game_state (round 0)
Your Bot → Server: {actions: [...]}
Server → Your Bot: game_state (round 1)
Your Bot → Server: {actions: [...]}
...
Server → Your Bot: game_over {score, items, orders}

The game runs for up to 300 rounds (500 on Nightmare) with a 120-second wall-clock limit (300s on Nightmare).
5 Difficulty Levels



Level
Grid
Bots
Aisles
Item Types
Order Size
Drop Zones




Easy
12×10
1
2
4
3-4
1


Medium
16×12
3
3
8
3-5
1


Hard
22×14
5
4
12
3-5
1


Expert
28×18
10
5
16
4-6
1


Nightmare
30×18
20
6
21
4-7
3



One map per difficulty. Item placement and orders change daily — same day, same game (deterministic).
Easy — Solo Pathfinding
One bot, small store. Focus on efficient routing.

Medium — Team Coordination
Three bots, larger store. Divide the work.

Hard — Multi-Agent Planning
Five bots, complex layout. Avoid duplicate work.

Expert — Swarm Management
Ten bots, massive store. Coordinate a swarm efficiently.

Nightmare — Total Chaos
Twenty bots, 3 drop-off zones, 500 rounds, 21 item types. The ultimate coordination challenge.
WebSocket Protocol
Connect via WebSocket to the URL provided when you request a game:
wss://game.ainm.no/ws?token=<jwt_token>

Get a token by clicking "Play" on a map at app.ainm.no/challenge.
Game State Message
Each round, you receive:
{
"type": "game_state",
"round": 42,
"max_rounds": 300,
"grid": {
"width": 16,
"height": 12,
"walls": [[1,1], [1,2], [3,1]]
},
"bots": [
{"id": 0, "position": [3, 7], "inventory": ["milk"]},
{"id": 1, "position": [5, 3], "inventory": []},
{"id": 2, "position": [10, 7], "inventory": ["bread", "eggs"]}
],
"items": [
{"id": "item_0", "type": "milk", "position": [2, 1]},
{"id": "item_1", "type": "bread", "position": [4, 1]}
],
"orders": [
{
"id": "order_0",
"items_required": ["milk", "bread", "eggs"],
"items_delivered": ["milk"],
"complete": false,
"status": "active"
},
{
"id": "order_1",
"items_required": ["cheese", "butter"],
"items_delivered": [],
"complete": false,
"status": "preview"
}
],
"drop_off": [1, 10],
"drop_off_zones": [[1, 10], [8, 10], [15, 10]],
"score": 12
}
Field Reference



Field
Type
Description




round
int
Current round (0-indexed)


max_rounds
int
Maximum rounds (300, or 500 for Nightmare)


grid.walls
int[][]
List of [x, y] wall positions


bots
object[]
All bots with id, position [x,y], and inventory


items
object[]
All items on shelves with id, type, and position [x,y]


orders
object[]
Active + preview orders (max 2 visible)


drop_off
int[]
[x, y] of the primary drop-off zone


drop_off_zones
int[][]
All drop-off zone positions (Nightmare has 3, others have 1). Deliver at any zone.


score
int
Current score



Bot Response
Send within 2 seconds:
{
"actions": [
{"bot": 0, "action": "move_up"},
{"bot": 1, "action": "pick_up", "item_id": "item_3"},
{"bot": 2, "action": "drop_off"}
]
}
Actions
Each bot performs one action per round:



Action
Extra Fields
Description




move_up
—
Move one cell up (y-1)


move_down
—
Move one cell down (y+1)


move_left
—
Move one cell left (x-1)


move_right
—
Move one cell right (x+1)


pick_up
item_id
Pick up item from adjacent shelf


drop_off
—
Deliver matching items at drop-off zone


wait
—
Do nothing



Invalid actions are treated as wait.

Pickup Rules

Bot must be adjacent (Manhattan distance 1) to the shelf with the item
Bot inventory must not be full (max 3 items)
item_id must match an item on the map


Dropoff Rules

Bot must be standing on the drop-off cell
Only items matching the active order are delivered
Non-matching items stay in inventory
When an order completes, the next order activates and remaining items are re-checked


Sequential Orders
Orders are revealed one at a time and keep generating:

Active order — the current order you must complete. You can deliver items for it.
Preview order — the next order. Visible but you cannot deliver to it yet. You can pre-pick items.
Infinite — when you complete an order, a new one appears. Orders never run out. Rounds are the only limit.

Scoring



Event
Points




Item delivered
+1


Order completed
+5 bonus



Your leaderboard score = sum of your best score on each of the 5 maps.
Constraints

300 rounds maximum per game (500 for Nightmare)
120 seconds wall-clock limit (300s for Nightmare)
3 items per bot inventory
Collision — bots block each other (no two on same tile, except spawn)
Full visibility — entire map visible every round
2-second timeout per round for your response
60-second cooldown between games, max 40 per hour and 300 per day per team
Disconnect = game over — no reconnect
Multiple drop-off zones — Nightmare has 3 interchangeable drop-off points

Coordinate System

Origin (0, 0) is the top-left corner
X increases to the right
Y increases downward

