import type { ReactNode } from "react";

interface WorkspaceProps {
  readonly className?: string;
  readonly title: string;
  readonly actions?: ReactNode;
  readonly children: ReactNode;
}

export function Workspace({
  className,
  title,
  actions,
  children,
}: WorkspaceProps) {
  return (
    <section className={`workspace ${className ?? ""}`} aria-label={title}>
      <header className="workspace-header">
        <div>
          <h1>{title}</h1>
        </div>
        {actions ? <div className="workspace-actions">{actions}</div> : null}
      </header>
      {children}
    </section>
  );
}

export function EmptyStage({
  title,
  detail,
}: {
  readonly title: string;
  readonly detail: string;
}) {
  return (
    <div className="empty-stage">
      <strong>{title}</strong>
      <span>{detail}</span>
    </div>
  );
}
