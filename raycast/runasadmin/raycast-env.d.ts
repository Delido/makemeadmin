/// <reference types="@raycast/api">

/* 🚧 🚧 🚧
 * This file is auto-generated from the extension's manifest.
 * Do not modify manually. Instead, update the `package.json` file.
 * 🚧 🚧 🚧 */

/* eslint-disable @typescript-eslint/ban-types */

type ExtensionPreferences = {
  /** Server-URL - Basis-URL des RunAsAdmin-Add-ons (ohne abschliessenden /). Zoraxy oder LAN. */
  "serverUrl": string,
  /** pc_token - Dasselbe Token wie in den Add-on-Optionen (pc_token). */
  "pcToken": string
}

/** Preferences accessible in all the extension's commands */
declare type Preferences = ExtensionPreferences

declare namespace Preferences {
  /** Preferences accessible in the `requests` command */
  export type Requests = ExtensionPreferences & {}
  /** Preferences accessible in the `overview` command */
  export type Overview = ExtensionPreferences & {}
}

declare namespace Arguments {
  /** Arguments passed to the `requests` command */
  export type Requests = {}
  /** Arguments passed to the `overview` command */
  export type Overview = {}
}

