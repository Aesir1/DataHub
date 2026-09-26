import { signIn } from "@/auth";
import { StartScreen } from "@/components/StartScreen";
import { getBranding } from "@/lib/branding.server";

export default function StartPage() {
  return (
    <StartScreen
      branding={getBranding()}
      action={async () => {
        "use server";
        await signIn("keycloak", { redirectTo: "/app" });
      }}
    />
  );
}
