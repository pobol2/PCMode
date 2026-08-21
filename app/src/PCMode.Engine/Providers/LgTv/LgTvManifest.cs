namespace PCMode.Engine;

/// <summary>
/// What this application tells a television it is, when it asks to be paired
/// with.
/// </summary>
/// <remarks>
/// WHY IT IS A BLOB AND NOT A BUILDER
///
/// This is the manifest webOS televisions have accepted for a decade, signed with
/// the signature that goes with it. It is not a structure to be composed: the
/// "signed" object and the signature are a matched pair, and rebuilding the object
/// from parts would produce something the signature no longer covers. So it is
/// stored exactly as it is sent, where it can be seen to be unmodified.
///
/// WHY THE PERMISSIONS ARE NOT TRIMMED
///
/// Asking for less looks tidier and is worse. The set of permissions is part of
/// what the user approves when they accept the prompt, and a television issues a
/// key against the set it was shown -- so narrowing it later means the stored key
/// stops being enough and everybody has to accept a second prompt they were not
/// expecting. This is the standard set, which is what the television's own remote
/// applications ask for, and what every maintained implementation of this protocol
/// sends.
///
/// LG A uses a handful of them: reading the power state, switching the power
/// off, switching inputs, reading which application is in front, reading and
/// writing picture settings, and showing the alert used to detect a signal.
/// The rest are present because removing them would cost a re-pairing and buy
/// nothing -- the television grants what it grants at pairing time, and this
/// application only ever sends the requests it has code for.
/// </remarks>
internal static class LgTvManifest
{
    /// <summary>The manifest, as it goes on the wire.</summary>
    public const string Json =
        """
        {
          "appVersion": "1.1",
          "manifestVersion": 1,
          "permissions": [
            "LAUNCH",
            "LAUNCH_WEBAPP",
            "APP_TO_APP",
            "CLOSE",
            "TEST_OPEN",
            "TEST_PROTECTED",
            "CONTROL_AUDIO",
            "CONTROL_DISPLAY",
            "CONTROL_INPUT_JOYSTICK",
            "CONTROL_INPUT_MEDIA_RECORDING",
            "CONTROL_INPUT_MEDIA_PLAYBACK",
            "CONTROL_INPUT_TV",
            "CONTROL_POWER",
            "CONTROL_TV_SCREEN",
            "READ_APP_STATUS",
            "READ_CURRENT_CHANNEL",
            "READ_INPUT_DEVICE_LIST",
            "READ_NETWORK_STATE",
            "READ_RUNNING_APPS",
            "READ_TV_CHANNEL_LIST",
            "WRITE_NOTIFICATION_TOAST",
            "READ_POWER_STATE",
            "READ_COUNTRY_INFO",
            "CONTROL_INPUT_TEXT",
            "CONTROL_MOUSE_AND_KEYBOARD",
            "READ_INSTALLED_APPS",
            "READ_SETTINGS"
          ],
          "signatures": [
            {
              "signature": "eyJhbGdvcml0aG0iOiJSU0EtU0hBMjU2Iiwia2V5SWQiOiJ0ZXN0LXNpZ25pbmctY2VydCIsInNpZ25hdHVyZVZlcnNpb24iOjF9.hrVRgjCwXVvE2OOSpDZ58hR+59aFNwYDyjQgKk3auukd7pcegmE2CzPCa0bJ0ZsRAcKkCTJrWo5iDzNhMBWRyaMOv5zWSrthlf7G128qvIlpMT0YNY+n/FaOHE73uLrS/g7swl3/qH/BGFG2Hu4RlL48eb3lLKqTt2xKHdCs6Cd4RMfJPYnzgvI4BNrFUKsjkcu+WD4OO2A27Pq1n50cMchmcaXadJhGrOqH5YmHdOCj5NSHzJYrsW0HPlpuAx/ECMeIZYDh6RMqaFM2DXzdKX9NmmyqzJ3o/0lkk/N97gfVRLW5hA29yeAwaCViZNCP8iC9aO0q9fQojoa7NQnAtw==",
              "signatureVersion": 1
            }
          ],
          "signed": {
            "appId": "com.lge.test",
            "created": "20140509",
            "localizedAppNames": {
              "": "LG Remote App",
              "ko-KR": "리모컨 앱",
              "zxx-XX": "ЛГ Rэmotэ AПП"
            },
            "localizedVendorNames": {
              "": "LG Electronics"
            },
            "permissions": [
              "TEST_SECURE",
              "CONTROL_INPUT_TEXT",
              "CONTROL_MOUSE_AND_KEYBOARD",
              "READ_INSTALLED_APPS",
              "READ_LGE_SDX",
              "READ_NOTIFICATIONS",
              "SEARCH",
              "WRITE_SETTINGS",
              "WRITE_NOTIFICATION_ALERT",
              "CONTROL_POWER",
              "READ_CURRENT_CHANNEL",
              "READ_RUNNING_APPS",
              "READ_UPDATE_INFO",
              "UPDATE_FROM_REMOTE_APP",
              "READ_LGE_TV_INPUT_EVENTS",
              "READ_TV_CURRENT_TIME"
            ],
            "serial": "2f930e2d2cfe083771f68e4fe7bb07",
            "vendorId": "com.lge"
          }
        }
        """;
}
