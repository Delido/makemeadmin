import { Action, ActionPanel, Color, Icon, List, Toast, showToast } from "@raycast/api";
import { useFetch } from "@raycast/utils";
import { authHeaders, decide, Decision, Overview, overviewUrl, PendingRequest } from "./api";

export default function Command() {
  const { data, isLoading, revalidate, error } = useFetch<Overview>(overviewUrl(), {
    headers: authHeaders(),
  });

  const pending = data?.pending ?? [];

  async function answer(req: PendingRequest, action: Decision, label: string) {
    const toast = await showToast({ style: Toast.Style.Animated, title: `${label} …` });
    try {
      await decide(req.id, action);
      toast.style = Toast.Style.Success;
      toast.title = `${req.user} an ${req.computer}: ${label}`;
      revalidate();
    } catch (e) {
      toast.style = Toast.Style.Failure;
      toast.title = "Fehlgeschlagen";
      toast.message = e instanceof Error ? e.message : String(e);
    }
  }

  return (
    <List isLoading={isLoading} searchBarPlaceholder="Offene Anfragen durchsuchen">
      {error ? (
        <List.EmptyView
          icon={{ source: Icon.Warning, tintColor: Color.Red }}
          title="Verbindung fehlgeschlagen"
          description={error.message}
        />
      ) : (
        <List.Section title="Offene Anfragen" subtitle={pending.length ? `${pending.length}` : undefined}>
          {pending.map((r) => (
            <List.Item
              key={r.id}
              icon={{ source: Icon.Clock, tintColor: Color.Yellow }}
              title={`${r.user} an ${r.computer}`}
              subtitle={r.reason || "möchte 15 Minuten Admin"}
              actions={
                <ActionPanel>
                  <Action
                    title="15 Min Erlauben"
                    icon={{ source: Icon.Check, tintColor: Color.Green }}
                    onAction={() => answer(r, "allow", "15 Min erlaubt")}
                  />
                  <Action
                    title="Immer Erlauben"
                    icon={{ source: Icon.Star, tintColor: Color.Blue }}
                    onAction={() => answer(r, "allow_always", "Immer erlaubt")}
                  />
                  <Action
                    title="Ablehnen"
                    icon={{ source: Icon.XMarkCircle, tintColor: Color.Red }}
                    style={Action.Style.Destructive}
                    onAction={() => answer(r, "deny", "Abgelehnt")}
                  />
                  <Action
                    title="Aktualisieren"
                    icon={Icon.ArrowClockwise}
                    shortcut={{ modifiers: ["cmd"], key: "r" }}
                    onAction={revalidate}
                  />
                </ActionPanel>
              }
            />
          ))}
        </List.Section>
      )}
      {!error && pending.length === 0 && !isLoading ? (
        <List.EmptyView
          icon={{ source: Icon.CheckCircle, tintColor: Color.Green }}
          title="Keine offenen Anfragen"
          description="Alles beantwortet."
        />
      ) : null}
    </List>
  );
}
