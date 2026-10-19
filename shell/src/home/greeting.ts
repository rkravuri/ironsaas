/** The part of the day a greeting names, by the hour on this browser's clock. */
export type DayPart = "morning" | "afternoon" | "evening";

/** Morning from five until noon, afternoon until six, evening the rest of the night. */
export function dayPart(hour: number): DayPart {
  if (hour >= 5 && hour < 12) {
    return "morning";
  }
  if (hour >= 12 && hour < 18) {
    return "afternoon";
  }
  return "evening";
}
