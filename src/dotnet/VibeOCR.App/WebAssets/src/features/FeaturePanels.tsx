export function Panel({
  label,
  title,
  className,
  children,
}: {
  readonly label?: string;
  readonly title: string;
  readonly className?: string;
  readonly children: React.ReactNode;
}) {
  return (
    <section
      className={`work-panel${className ? ` ${className}` : ""}`}
      aria-label={title}
    >
      <header className="panel-heading">
        {label ? <span>{label}</span> : null}
        <h2>{title}</h2>
      </header>
      <div className="panel-content">{children}</div>
    </section>
  );
}

export function StatusLine({
  children,
}: {
  readonly children: React.ReactNode;
}) {
  return (
    <output className="status-line" aria-live="polite">
      {children}
    </output>
  );
}
