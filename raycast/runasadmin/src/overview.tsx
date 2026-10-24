import {
  Action,
  ActionPanel,
  Alert,
  Color,
  Icon,
  List,
  Toast,
  confirmAlert,
  showToast,
} from "@raycast/api";
import { useFetch } from "@raycast/utils";
import { authHeaders, cancel, Device, Overview, overviewUrl, removeApp } from "./api";

export default function Command() {
  const { data, isLoading, revalidate, error } = useFetch<Overview>(overviewUrl(), {
    headers: authHeaders(),
  });

  const devices = data?.devices ?? [];

  async function doCancel(d: Device) {
    const ok = await confirmAlert({
      title: `Not-Aus für ${d.name}?`,
      message: "Die laufenden Adminrechte werden beim nächsten Kontakt des PCs sofort beendet.",
      primaryAction: { title: "Not-Aus auslösen", style: Alert.ActionStyle.Destructive },
    });
    if (!ok) return;
    const toast = await showToast({ style: Toast.Style.Animated, title: "Not-Aus …" });
    try {
      await cancel(d.name);
      toast.style = Toast.Style.Success;
      toast.title = `Not-Aus für ${d.name} ausgelöst`;
      revalidate();
    } catch (e) {
      toast.style = Toast.Style.Failure;
      toast.title = "Fehlgeschlagen";
      toast.message = e instanceof Error ? e.message : String(e);
    }
  }

  async function doRemove(d: Device, app: string) {
    const ok = await confirmAlert({
      title: `„${app}" entfernen?`,
      message: `Die Freigabe wird von ${d.name} entfernt (beim nächsten Kontakt).`,
      primaryAction: { title: "Entfernen", style: Alert.ActionStyle.Destructive },
    });
    if (!ok) return;
    const toast = await showToast({ style: Toast.Style.Animated, title: "Entferne …" });
    try {
      await removeApp(d.name, app);
      toast.style = Toast.Style.Success;
      toast.title = `„${app}" von ${d.name} entfernt`;
      revalidate();
    } catch (e) {
      toast.style = Toast.Style.Failure;
      toast.title = "Fehlgeschlagen";
      toast.message = e instanceof Error ? e.message : String(e);
    }
  }

  return (
    <List isLoading={isLoading} searchBarPlaceholder="Geräte durchsuchen">
      {error ? (
        <List.EmptyView
          icon={{ source: Icon.Warning, tintColor: Color.Red }}
          title="Verbindung fehlgeschlagen"
          description={error.message}
        />
      ) : (
        devices.map((d) => {
          const accessories: List.Item.Accessory[] = [];
          if (d.admin_active) {
            accessories.push({ tag: { value: "Admin aktiv", color: Color.Orange } });
          }
          accessories.push({ tag: `${d.apps.length} App${d.apps.length === 1 ? "" : "s"}` });
          accessories.push({ text: `v${d.version}` });

          return (
            <List.Item
              key={d.name}
              icon={{
                source: d.online ? Icon.CircleFilled : Icon.Circle,
                tintColor: d.online ? Color.Green : Color.SecondaryText,
              }}
              title={d.name}
              subtitle={d.online ? "online" : `zuletzt gesehen ${d.last_seen_text}`}
              accessories={accessories}
              actions={
                <ActionPanel>
                  <Action
                    title="Not-Aus (Adminrechte Beenden)"
                    icon={{ source: Icon.Bolt, tintColor: Color.Red }}
                    style={Action.Style.Destructive}
                    onAction={() => doCancel(d)}
                  />
                  {d.apps.length > 0 ? (
                    <ActionPanel.Submenu
                      title="App-Freigabe Entfernen"
                      icon={Icon.Trash}
                      shortcut={{ modifiers: ["cmd"], key: "backspace" }}
                    >
                      {d.apps.map((app) => (
                        <Action key={app} title={app} onAction={() => doRemove(d, app)} />
                      ))}
                    </ActionPanel.Submenu>
                  ) : null}
                  <Action
                    title="Aktualisieren"
                    icon={Icon.ArrowClockwise}
                    shortcut={{ modifiers: ["cmd"], key: "r" }}
                    onAction={revalidate}
                  />
                </ActionPanel>
              }
            />
          );
        })
      )}
      {!error && devices.length === 0 && !isLoading ? (
        <List.EmptyView
          icon={Icon.Desktop}
          title="Noch kein PC gemeldet"
          description="Sobald sich ein Kinder-PC meldet, erscheint er hier."
        />
      ) : null}
    </List>
  );
}
