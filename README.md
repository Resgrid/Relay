Resgrid Relay
===========================

Resgrid Relay is a desktop and CLI application for creating calls in the Resgrid system based on watchers (audio, file, database, etc).

*********

[![Build status](https://ci.appveyor.com/api/projects/status/github/resgrid/relay?svg=true)](https://ci.appveyor.com/api/projects/status/github/resgrid/relay)

About Resgrid
-------------
Resgrid is a software as a service (SaaS) logistics, management and communications platform for first responders, volunteer fire departments, career fire, EMS, Search and Rescue (SAR), public safety, HAZMAT, CERT, disaster response, etc.

[Sign up for your free Resgrid Account Today!](https://resgrid.com)

## System Requirements ##

* Windows 7 or newer
* .Net Framework 4.6.2
* 1.8Ghz Single Core Processor
* 8GB of RAM
* 2GB of Free Disk Space
* For Scanner audio a Scanner with an Audio Line Out i.e. [WS1065](https://amzn.to/2Kuck8k) is needed

## Configuration

```json
// settings.json
{
  "InputDevice": 0,
  "AudioLength": 60,
  "ApiUrl": "https://api.resgrid.com",
  "Username": "TEST",
  "Password": "TEST",
  "Multiple": false,
  "Tolerance": 100,
  "Threshold": -40,
  "Watchers": [
    {
	  "Id": "ee188c37-09f4-47c0-9ff1-fe34c9d6a5f1",
      "Name": "Station 1",
      "Active": true,
      "Code": null,
      "Type": 0,
      "Eval": 0,
      "Triggers": [
        {
          "Frequency1": 524.0,
          "Frequency2": 794.0,
          "Time": 500,
          "Count": 2
        }
      ]
    },
	...
  ]
}
```

## Settings

### Settings.json Values
<table>
  <tr>
    <th>Setting</th>
    <th>Description</th>
  </tr>
  <tr>
    <td>InputDevice</td>
    <td>
      The Audio Input device that system will listen to. It's recommend this is a hard Line In or Stereo Mix input and not a Mic input
    </td>
  </tr>
  <tr>
    <td>AudioLength</td>
    <td>
      Time of time in SECONDS to record the dispatch audio for
    </td>
  </tr>
  <tr>
    <td>ApiUrl</td>
    <td>
      The URL to talk to the Resgrid API (Services) for our hosted production system this is "https://api.resgrid.com"
    </td>
  </tr>
  <tr>
    <td>Username</td>
    <td>
      Resgrid system login Username that can create calls
    </td>
  </tr>
  <tr>
    <td>Password</td>
    <td>
      Resgrid system login Password for the Username above
    </td>
  </tr>
  <tr>
    <td>Multiple</td>
    <td>
      Once a tone is Detected, do you want 1 call created and the Groups dispatched for it, or a call created in Resgrid for each watcher. If Multiple is false, one call will only be created and each Group (per watcher) will be dispatched as part of that call, if Multiple is true each watcher creates a call in Resgrid.
    </td>
  </tr>
  <tr>
    <td>Tolerance</td>
    <td>
      The relative power of the tone frequency to trigger. This value should be between 50 and 250, ideally at or around 100 (the default). If your getting false triggers try increasing this value.
    </td>
  </tr>
  <tr>
    <td>Threshold</td>
    <td>
      Decibel dB value for silence detection, default is -40. Depending on how loud the background audio or static is this value may need to be raised to cut out the static.
    </td>
  </tr>
  <tr>
    <td>Debug</td>
    <td>
      Enable or Disable debug mode. Debug should only be enabled when trying to analyze an issue.
    </td>
  </tr>
  <tr>
    <td>Watchers</td>
    <td>
      An Array of Watcher Objects
    </td>
  </tr>
</table>

### Watcher Settings Values
<table>
  <tr>
    <th>Setting</th>
    <th>Description</th>
  </tr>
  <tr>
    <td>Id</td>
    <td>
      Unique GUID\UUID for the watcher, this value is used to queue and dequeue watchers and verify if one is already running. This value must be unique for every Watcher in the array.
    </td>
  </tr>
  <tr>
    <td>Name</td>
    <td>
      Name of the group that this watcher is for. Seeming Watchers are tied to Resgrid groups it's usually best to just put the group name in here.
    </td>
  </tr>
  <tr>
    <td>Active</td>
    <td>
      Can be true or false. Determines if this watcher is active and it's triggers should be monitored. 
    </td>
  </tr>
  <tr>
    <td>Code</td>
    <td>
      Your department groups dispatch code. You get this value from the Stations & Groups section of the website, and it's the alphanumeric code in front of @groups.resgrid.com. Do not include anything other then the 6 character code.
    </td>
  </tr>
  <tr>
    <td>Type</td>
    <td>
      1 = Department, 2 = Group
    </td>
  </tr>
  <tr>
    <td>Eval</td>
    <td>
      Unused, leave 0
    </td>
  </tr>
  <tr>
    <td>Triggers</td>
    <td>
      Array of triggers
    </td>
  </tr>
</table>

### Triggers Settings Values
<table>
  <tr>
    <th>Setting</th>
    <th>Description</th>
  </tr>
  <tr>
    <td>Frequency1</td>
    <td>
      The first (or only) tone frequency to monitor
    </td>
  </tr>
  <tr>
    <td>Frequency2</td>
    <td>
      The second tone frequency to monitor
    </td>
  </tr>
  <tr>
    <td>Time</td>
    <td>
      Time in milliseconds the tone need to run for to be triggered. If your tones run for 1 second (1000 milliseconds) each you should set this value to 750 or 500. If your getting false positives increase this value a bit. But tone length detection can be difficult if there is competing traffic or noise. So a lower value is 'safer'.
    </td>
  </tr>
  <tr>
    <td>Count</td>
    <td>
      1 or 2, the number of distinct tones your trigger has.
    </td>
  </tr>
</table>

## Environment Setup ##

The following prerequisites are required.

* Visual Studio


## Compilation ##



## Development ##



## Solution ##



## Dependencies ##


## Notes ##


## Author's ##
* Shawn Jackson (Twitter: @DesignLimbo Blog: http://designlimbo.com)
* Jason Jarrett (Twitter: @staxmanade Blog: http://staxmanade.com)

## License ##
[Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0)

## Acknowledgments

Resgrid Relay makes use of the following OSS projects:

- Consolas released under the BSD 2-Clause license: https://github.com/rickardn/Consolas/blob/develop/LICENSE
- NAudio released under the Microsoft Public License: https://github.com/naudio/NAudio/blob/master/license.txt
- DtmfDetection released under the GNU Lesser GPL v3.0 License: https://github.com/Resgrid/DtmfDetection/blob/master/LICENSE