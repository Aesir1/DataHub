import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ReadingsTable } from "./ContainerBrowser";

describe("ReadingsTable", () => {
  it("renders readings in the order the Api returns them, in Celsius", () => {
    render(
      <ReadingsTable
        readings={[
          { id: "1", timestampUtc: "2026-09-25T10:00:00Z", celsius: 5 },
          { id: "2", timestampUtc: "2026-09-25T11:00:00Z", celsius: -1.25 },
        ]}
      />,
    );

    const rows = screen.getAllByRole("row").slice(1);
    expect(rows.map((r) => within(r).getAllByRole("cell")[1]?.textContent)).toEqual(["5.0", "-1.3"]);
    expect(rows[0]?.querySelector("time")?.getAttribute("dateTime")).toBe("2026-09-25T10:00:00Z");
  });
});
