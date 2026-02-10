# **Fika Discord Presence**

Displays live Fika server status in Discord using a webhook.
Shows online players, raid status, activity, and optional Weekly Boss information parsed from SPT logs (if ABPS is installed).

---

## **Example**

![Description](https://i.ibb.co/1f1B9GQW/image.png)


---

## **Features**

* Live player list from the Fika API
* Raid / out-of-raid activity detection
* Side display (*PMC / Scav*) during raids.
* Activity duration tracking
* Weekly Boss detection from SPT logs (if ABPS is installed)
* Auto-creates and edits a single persistent Discord message
* Live config reload *(no restart required for most settings)*
* Extremely customizable (Icons, colors, weekly boss display, names of categories, refresh rate...)

---

## **Requirements**

* Fika
* Discord webhook URL

---

## **Installation**

1. Place the mod folder inside your Tarkov install folder.
2. Open config.json.
3. Insert:
   * Discord.WebhookUrl (https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks)
   * Fika.ApiKey (Found in SPTInstallFolder\SPT\user\mods\fika-server\assets\configs\fika.jsonc)
   * LogFolderPath (X:\\Your\\Tarkov\\Folder\\SPT\\user\\logs\\spt)
4. Start the SPT server.


Thumbnail by agus raharjo, inverted by me.
