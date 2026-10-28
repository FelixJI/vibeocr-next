import type { ReactNode } from "react";

export function Panel({
  title,
  className,
  actions,
  children,
}: {
  readonly title: string;
  readonly className?: string;
  readonly actions?: ReactNode;
  readonly children: ReactNode;
}) {
  return (
    <section
      className={`work-panel${className ? ` ${className}` : ""}`}
      aria-label={title}
    >
      <header className="panel-heading">
        <h2>{title}</h2>
        {actions ? <div className="panel-actions">{actions}</div> : null}
      </header>
      <div className="panel-content">{children}</div>
    </section>
  );
}

export function StatusLine({ children }: { readonly children: ReactNode }) {
  return (
    <output className="status-line" aria-live="polite">
      {children}
    </output>
  );
}
