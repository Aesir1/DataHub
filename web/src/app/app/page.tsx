import { Suspense } from "react";
import { ContainerBrowser } from "@/components/ContainerBrowser";

export const metadata = { title: "Containers" };

export default function ContainersPage() {
  return (
    <Suspense>
      <ContainerBrowser />
    </Suspense>
  );
}
