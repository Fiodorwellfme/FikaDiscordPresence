# **Fika Discord Presence**

Displays live Fika server status in Discord using a webhook.
Shows online players, raid status, activity, and optional Weekly Boss information parsed from SPT logs (if ABPS is installed).

---

## **Features**

* Live player list from the Fika API
* Raid / out-of-raid activity detection
* Side display (*PMC / Scav*)
* Activity duration tracking
* Weekly Boss detection from SPT logs
* Auto-creates and edits a single persistent Discord message
* Live config reload *(no restart required for most settings)*

---

## **Requirements**

* SPT 4.x
* Fika
* Discord webhook URL
* ABPS if you want the Weekly boss tracking to work.

---

## **Installation**

1. Place the mod folder inside your Tarkov install folder.
2. Open config.json.
3. Insert:
   * Discord.WebhookUrl
   * Fika.ApiKey
   * LogFolderPath
4. Start the SPT server.
