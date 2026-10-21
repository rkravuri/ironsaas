import { useMemo } from "react";
import qrcode from "qrcode-generator";

interface QrCodeProps {
  /** What the code holds: an authenticator app's otpauth URI. */
  value: string;
  /** Its accessible name: what it is for, never what it holds, which is the secret. */
  label: string;
  size?: number;
}

/**
 * A QR code drawn as one SVG path (qrcode-generator, MIT): dark modules on a light ground with a quiet zone of four, in
 * both schemes, since a camera reads it so. Medium error correction, the usual choice for a code on a screen.
 */
export function QrCode({ value, label, size = 200 }: QrCodeProps) {
  const { path, count } = useMemo(() => {
    const code = qrcode(0, "M");
    code.addData(value);
    code.make();
    const modules = code.getModuleCount();
    let d = "";
    for (let row = 0; row < modules; row++) {
      for (let column = 0; column < modules; column++) {
        if (code.isDark(row, column)) {
          d += `M${column + 4} ${row + 4}h1v1h-1z`;
        }
      }
    }
    return { path: d, count: modules + 8 };
  }, [value]);

  return (
    <svg
      role="img"
      aria-label={label}
      width={size}
      height={size}
      viewBox={`0 0 ${count} ${count}`}
      shapeRendering="crispEdges"
      data-qr-code
    >
      <rect width={count} height={count} style={{ fill: "var(--iron-qr-paper)" }} />
      <path d={path} style={{ fill: "var(--iron-qr-ink)" }} />
    </svg>
  );
}
