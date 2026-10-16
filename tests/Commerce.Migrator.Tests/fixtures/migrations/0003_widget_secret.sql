CREATE TABLE IF NOT EXISTS public.widget_secrets (value text NOT NULL);
DELETE FROM public.widget_secrets;
INSERT INTO public.widget_secrets (value) VALUES ('__APP_RUNTIME_PASSWORD__');
